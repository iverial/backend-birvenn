using BirvennApi.Data;
using BirvennApi.Dtos;
using BirvennApi.Infrastructure;
using BirvennApi.Models;
using BirvennApi.Options;
using BirvennApi.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BirvennApi.Services;

public class OrderService(
    AppDbContext db,
    OrderLifecycle lifecycle,
    PaymentProcessor processor,
    MercadoPagoClient mercadoPago,
    IOptions<PaymentsOptions> payments,
    IOptions<CommerceOptions> commerce)
{
    // Estados a los que puede pasar un pedido a mano desde el panel. "Pagado" no esta: solo lo confirma la pasarela
    // o confirm-payment (transferencia).
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Transitions = new()
    {
        [OrderStatus.Pending] = [OrderStatus.Cancelled],
        [OrderStatus.Paid] = [OrderStatus.Preparing, OrderStatus.Cancelled],
        [OrderStatus.Preparing] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.Delivered],
        [OrderStatus.Delivered] = [],
        [OrderStatus.Cancelled] = [],
    };

    /// <summary>El id publico es la llave: funciona para invitados y para usuarios.</summary>
    public async Task<Order> GetByPublicIdAsync(string publicId, CancellationToken ct) =>
        await db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.PublicId == publicId, ct)
        ?? throw AppException.NotFound("Pedido no encontrado.", "order_not_found");

    public async Task<List<Order>> ListForUserAsync(Guid userId, CancellationToken ct) =>
        await db.Orders.AsNoTracking().Include(o => o.Items).Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);

    /// <summary>
    /// Vuelve a consultar a Mercado Pago el estado del pago de un pedido. Lo llama el front al volver del checkout
    /// (por si el webhook todavia no llego) y sirve tambien cuando el webhook no es alcanzable (desarrollo local).
    /// </summary>
    public async Task<Order> ReconcileAsync(string publicId, string? paymentId, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.Payments).FirstOrDefaultAsync(o => o.PublicId == publicId, ct)
                    ?? throw AppException.NotFound("Pedido no encontrado.", "order_not_found");

        var canAsk = payments.Value.Provider.Equals("MercadoPago", StringComparison.OrdinalIgnoreCase)
                     && order.PaymentMethod != PaymentMethodId.Transfer
                     && order.Status == OrderStatus.Pending;
        if (!canAsk) return order;

        List<MpPayment> found;
        if (!string.IsNullOrWhiteSpace(paymentId))
        {
            var payment = await mercadoPago.GetPaymentAsync(paymentId.Trim(), ct)
                          ?? throw AppException.NotFound("No encontramos ese pago.", "payment_not_found");
            // Nunca se confia en el id que manda el navegador: tiene que ser un pago de ESTE pedido.
            if (payment.ExternalReference != order.Id.ToString())
                throw AppException.Unprocessable("Ese pago no corresponde a este pedido.", "payment_mismatch");
            found = [payment];
        }
        else
        {
            found = await mercadoPago.SearchPaymentsAsync(order.Id.ToString(), ct);
        }

        // Cronologico: el ultimo estado informado es el que queda.
        foreach (var payment in found.OrderBy(p => p.Id)) await processor.ApplyAsync(MercadoPagoSignature.ToUpdate(payment), ct);

        return order;
    }

    // ---------- Panel interno ----------

    public async Task<PagedOrders> PageAsync(OrderStatus? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Orders.AsNoTracking().Include(o => o.Items).AsQueryable();
        if (status is { } s) query = query.Where(o => o.Status == s);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim().ToLowerInvariant();
            query = query.Where(o => o.Number.ToLower().Contains(needle) || o.Email.Contains(needle));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(o => o.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedOrders([.. items.Select(o => o.ToDto())], total, page, pageSize);
    }

    public async Task<Order> SetStatusAsync(string publicId, OrderStatus next, string? trackingCode, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.PublicId == publicId, ct)
                    ?? throw AppException.NotFound("Pedido no encontrado.", "order_not_found");

        if (!Transitions[order.Status].Contains(next))
            throw AppException.Conflict($"No se puede pasar un pedido de {order.Status} a {next}.", "invalid_transition");

        if (next == OrderStatus.Cancelled)
        {
            await lifecycle.CancelAsync(order, "Cancelado por el equipo.", ct);
        }
        else
        {
            order.Status = next;
            if (!string.IsNullOrWhiteSpace(trackingCode)) order.TrackingCode = trackingCode.Trim();
            await db.SaveChangesAsync(ct);
        }

        return order;
    }

    /// <summary>Confirma a mano una transferencia ya recibida en la cuenta.</summary>
    public async Task<Order> ConfirmTransferAsync(string publicId, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Items).Include(o => o.Payments).FirstOrDefaultAsync(o => o.PublicId == publicId, ct)
                    ?? throw AppException.NotFound("Pedido no encontrado.", "order_not_found");

        if (order.Status != OrderStatus.Pending)
            throw AppException.Conflict("Solo se puede confirmar el pago de un pedido pendiente.", "invalid_transition");

        // Un pedido cancelado por vencimiento ya devolvio el stock; por eso solo se confirma si sigue pendiente.
        order.Payments.Add(new Payment
        {
            OrderId = order.Id,
            Provider = "transfer",
            ExternalId = $"transfer-{order.Number}",
            Status = "approved",
            Amount = order.Total,
            Currency = commerce.Value.Currency,
            MethodType = "bank_transfer",
        });
        OrderLifecycle.MarkPaid(order);
        order.PaymentNote = "Transferencia confirmada por el equipo.";
        await db.SaveChangesAsync(ct);
        return order;
    }

    public async Task SetStockAsync(string productId, string size, int quantity, CancellationToken ct)
    {
        var product = await db.Products.Include(p => p.Stock).FirstOrDefaultAsync(p => p.Id == productId, ct)
                      ?? throw AppException.NotFound("Producto no encontrado.", "product_not_found");
        if (!product.Sizes.Contains(size)) throw AppException.Unprocessable($"El producto no tiene talle {size}.", "size_unavailable");

        var row = product.Stock.FirstOrDefault(s => s.Size == size);
        if (row is null) product.Stock.Add(new ProductStock { ProductId = product.Id, Size = size, Quantity = quantity });
        else row.Quantity = quantity;
        await db.SaveChangesAsync(ct);
    }
}
