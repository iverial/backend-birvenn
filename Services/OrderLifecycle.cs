using BirvennApi.Data;
using BirvennApi.Infrastructure;
using BirvennApi.Models;
using Microsoft.EntityFrameworkCore;

namespace BirvennApi.Services;

/// <summary>
/// Todo lo que cambia el estado fisico de un pedido: reservar y devolver stock, cancelar, vencer.
/// El stock se descuenta al crear el pedido (reserva) con un UPDATE atomico condicionado, asi dos compras
/// simultaneas de la ultima unidad no pueden pasar las dos.
/// </summary>
public class OrderLifecycle(AppDbContext db, ILogger<OrderLifecycle> logger)
{
    public record StockLine(string ProductId, string Size, int Quantity, string Name);

    /// <summary>
    /// Descuenta stock de forma atomica. Si alguna linea no alcanza devuelve las que ya habia descontado
    /// y lanza 409, asi nunca queda una reserva a medias aunque el que llama no haga rollback.
    /// </summary>
    public async Task ReserveStockAsync(IEnumerable<StockLine> lines, CancellationToken ct)
    {
        var reserved = new List<StockLine>();
        foreach (var line in lines)
        {
            var (productId, size, quantity) = (line.ProductId, line.Size, line.Quantity);
            var affected = await db.ProductStocks
                .Where(s => s.ProductId == productId && s.Size == size && s.Quantity >= quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity - quantity), ct);

            if (affected == 0)
            {
                await ReturnStockAsync(reserved, ct);
                throw AppException.Conflict($"Sin stock suficiente de {line.Name} ({line.Size}).", "out_of_stock");
            }

            reserved.Add(line);
        }
    }

    public Task RestoreStockAsync(Order order, CancellationToken ct) =>
        ReturnStockAsync(order.Items.Select(i => new StockLine(i.ProductId, i.Size, i.Quantity, i.Name)), ct);

    private async Task ReturnStockAsync(IEnumerable<StockLine> lines, CancellationToken ct)
    {
        foreach (var line in lines)
        {
            var (productId, size, quantity) = (line.ProductId, line.Size, line.Quantity);
            await db.ProductStocks
                .Where(s => s.ProductId == productId && s.Size == size)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity + quantity), ct);
        }
    }

    public static void MarkPaid(Order order)
    {
        order.Status = OrderStatus.Paid;
        order.PaidAt ??= DateTime.UtcNow;
    }

    /// <summary>Cancela un pedido, devuelve el stock y libera el cupon personal que habia consumido.</summary>
    public Task CancelAsync(Order order, string reason, CancellationToken ct) => InTransactionAsync(async () =>
    {
        // El cambio de estado es un UPDATE condicionado: si dos procesos (dos instancias del worker, el webhook y
        // un admin) cancelan a la vez, solo uno lo consigue y por lo tanto solo uno devuelve el stock.
        var orderId = order.Id;
        var now = DateTime.UtcNow;
        var changed = await db.Orders
            .Where(o => o.Id == orderId && o.Status != OrderStatus.Cancelled)
            .ExecuteUpdateAsync(o => o
                .SetProperty(x => x.Status, OrderStatus.Cancelled)
                .SetProperty(x => x.CancelledAt, now)
                .SetProperty(x => x.PaymentNote, reason)
                // Libera la clave de idempotencia para que el cliente pueda reintentar con la misma.
                .SetProperty(x => x.ClientOrderId, (string?)null), ct);

        if (changed == 0) return;

        await RestoreStockAsync(order, ct);

        await db.Coupons
            .Where(c => c.UsedByOrderId == orderId)
            .ExecuteUpdateAsync(c => c.SetProperty(x => x.UsedAt, (DateTime?)null).SetProperty(x => x.UsedByOrderId, (Guid?)null), ct);

        // La entidad en memoria quedo desactualizada por el UPDATE directo.
        await db.Entry(order).ReloadAsync(ct);
        logger.LogInformation("Pedido {Number} cancelado: {Reason}", order.Number, reason);
    }, ct);

    /// <summary>Intenta volver a reservar el stock de un pedido ya cancelado (pago aprobado fuera de plazo).</summary>
    public async Task<bool> TryReserveForOrderAsync(Order order, CancellationToken ct)
    {
        try
        {
            await ReserveStockAsync(order.Items.Select(i => new StockLine(i.ProductId, i.Size, i.Quantity, i.Name)), ct);
            return true;
        }
        catch (AppException)
        {
            return false;
        }
    }

    /// <summary>Asocia a la cuenta los pedidos de invitado hechos con el mismo email (ver CommerceOptions.ClaimGuestOrdersByEmail).</summary>
    public Task<int> ClaimGuestOrdersAsync(string email, Guid userId, CancellationToken ct) =>
        db.Orders.Where(o => o.UserId == null && o.Email == email)
            .ExecuteUpdateAsync(o => o.SetProperty(x => x.UserId, (Guid?)userId), ct);

    /// <summary>Vence pedidos sin pagar. Si hay un pago en curso en la pasarela, espera a que se resuelva.</summary>
    public async Task<int> ExpirePendingAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var due = await db.Orders
            .Include(o => o.Items)
            .Include(o => o.Payments)
            .Where(o => o.Status == OrderStatus.Pending && o.ExpiresAt < now)
            .OrderBy(o => o.ExpiresAt)
            .Take(100)
            .ToListAsync(ct);

        var cancelled = 0;
        foreach (var order in due)
        {
            var inFlight = order.Payments.Any(p =>
                (p.Status is "pending" or "in_process" or "authorized") && p.UpdatedAt > now.AddHours(-24));
            if (inFlight) continue;

            await CancelAsync(order, "Venció el plazo para pagar.", ct);
            cancelled++;
        }

        return cancelled;
    }

    public async Task InTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await work();
        await tx.CommitAsync(ct);
    }
}
