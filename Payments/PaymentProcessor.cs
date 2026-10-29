using BirvennApi.Data;
using BirvennApi.Models;
using BirvennApi.Services;
using Microsoft.EntityFrameworkCore;

namespace BirvennApi.Payments;

/// <summary>Informacion de un pago, ya independiente de la pasarela.</summary>
public record PaymentUpdate(
    string Provider,
    string ExternalId,
    string Status,
    string? StatusDetail,
    decimal? Amount,
    string? Currency,
    string? MethodType,
    string ExternalReference);

/// <summary>
/// Aplica a un pedido lo que informa la pasarela. Es idempotente: el webhook puede llegar repetido,
/// fuera de orden o ademas del retorno del cliente (reconcile) y el resultado es el mismo.
/// </summary>
public class PaymentProcessor(AppDbContext db, OrderLifecycle lifecycle, ILogger<PaymentProcessor> logger)
{
    public async Task<Order?> ApplyAsync(PaymentUpdate update, CancellationToken ct)
    {
        if (!Guid.TryParse(update.ExternalReference, out var orderId))
        {
            logger.LogWarning("Pago {Provider}/{PaymentId} sin external_reference valido", update.Provider, update.ExternalId);
            return null;
        }

        var order = await db.Orders.Include(o => o.Items).Include(o => o.Payments).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            logger.LogWarning("Pago {Provider}/{PaymentId} para un pedido inexistente {OrderId}", update.Provider, update.ExternalId, orderId);
            return null;
        }

        await lifecycle.InTransactionAsync(async () =>
        {
            Record(order, update);

            if (update.Status == "approved") await ApproveAsync(order, update, ct);
            else if (update.Status is "refunded" or "charged_back") Reverse(order, update);

            await db.SaveChangesAsync(ct);
        }, ct);

        return order;
    }

    private static void Record(Order order, PaymentUpdate update)
    {
        var payment = order.Payments.FirstOrDefault(p => p.Provider == update.Provider && p.ExternalId == update.ExternalId);
        if (payment is null)
        {
            payment = new Payment { OrderId = order.Id, Provider = update.Provider, ExternalId = update.ExternalId };
            order.Payments.Add(payment);
        }

        payment.Status = update.Status;
        payment.StatusDetail = update.StatusDetail;
        payment.Amount = update.Amount ?? payment.Amount;
        payment.Currency = update.Currency ?? payment.Currency;
        payment.MethodType = update.MethodType ?? payment.MethodType;
        payment.UpdatedAt = DateTime.UtcNow;
    }

    private async Task ApproveAsync(Order order, PaymentUpdate update, CancellationToken ct)
    {
        // Nunca se da por pagado un monto distinto al del pedido.
        if (update.Amount is { } paid && Math.Abs(paid - order.Total) > 0.01m)
        {
            order.PaymentNote = $"Monto aprobado ({paid}) distinto al del pedido ({order.Total}). Revisar a mano.";
            logger.LogError("Pedido {Number}: {Provider}/{PaymentId} aprobado por {Paid} pero el total es {Total}",
                order.Number, update.Provider, update.ExternalId, paid, order.Total);
            return;
        }

        switch (order.Status)
        {
            case OrderStatus.Pending:
                OrderLifecycle.MarkPaid(order);
                order.TransactionId = update.ExternalId;
                logger.LogInformation("Pedido {Number} pagado ({Provider}/{PaymentId})", order.Number, update.Provider, update.ExternalId);
                break;

            case OrderStatus.Cancelled:
                // Pago aprobado despues de que el pedido venciera: se intenta recuperar si todavia hay stock.
                if (await lifecycle.TryReserveForOrderAsync(order, ct))
                {
                    order.Status = OrderStatus.Pending;
                    order.CancelledAt = null;
                    OrderLifecycle.MarkPaid(order);
                    order.TransactionId = update.ExternalId;
                    order.PaymentNote = "Pago aprobado después del vencimiento; se recuperó el pedido.";
                    logger.LogWarning("Pedido {Number} recuperado tras pago tardío", order.Number);
                }
                else
                {
                    order.PaymentNote = "Pago aprobado sobre un pedido vencido y sin stock: hay que reembolsar.";
                    logger.LogError("Pedido {Number}: pago {PaymentId} aprobado sobre pedido vencido sin stock. REEMBOLSO MANUAL", order.Number, update.ExternalId);
                }
                break;
        }
    }

    private void Reverse(Order order, PaymentUpdate update)
    {
        order.PaymentNote = update.Status == "refunded" ? "Pago reembolsado." : "Contracargo informado por la pasarela.";

        // Si todavia no salio de la fabrica se cancela; si ya fue despachado solo queda la nota para revisar.
        if (order.Status is OrderStatus.Paid or OrderStatus.Preparing)
        {
            order.Status = OrderStatus.Cancelled;
            order.CancelledAt = DateTime.UtcNow;
            order.ClientOrderId = null;
        }

        logger.LogWarning("Pedido {Number}: {Status} en {Provider}/{PaymentId}", order.Number, update.Status, update.Provider, update.ExternalId);
    }
}
