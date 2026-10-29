using System.Text.Json;
using BirvennApi.Data;
using BirvennApi.Dtos;
using BirvennApi.Infrastructure;
using BirvennApi.Models;
using BirvennApi.Options;
using BirvennApi.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BirvennApi.Controllers;

[ApiController]
[Route("api/payments")]
public class PaymentsController(
    MercadoPagoClient mercadoPago,
    PaymentProcessor processor,
    AppDbContext db,
    IOptions<PaymentsOptions> payments,
    IOptions<MercadoPagoOptions> mpOptions,
    IOptions<CommerceOptions> commerce,
    IHostEnvironment env,
    ILogger<PaymentsController> logger) : ControllerBase
{
    /// <summary>
    /// Notificaciones de Mercado Pago (Webhooks). Pasos: 1) verificar la firma, 2) NO confiar en el cuerpo:
    /// consultar el pago a la API de Mercado Pago, 3) aplicar el resultado al pedido de forma idempotente.
    /// Responde 200 rapido; si algo falla de nuestro lado responde 5xx para que Mercado Pago reintente.
    /// </summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    [DisableRateLimiting]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        if (!payments.Value.Provider.Equals("MercadoPago", StringComparison.OrdinalIgnoreCase)) return NotFound();

        // Mercado Pago manda data.id y type por query string; el cuerpo trae lo mismo.
        var dataId = Request.Query["data.id"].ToString();
        var type = Request.Query["type"].ToString();
        if (type.Length == 0) type = Request.Query["topic"].ToString();

        var signature = Request.Headers["x-signature"].ToString();
        var requestId = Request.Headers["x-request-id"].ToString();

        if (!MercadoPagoSignature.Verify(signature, requestId, dataId, mpOptions.Value.WebhookSecret))
        {
            logger.LogWarning("Webhook de Mercado Pago con firma invalida (request-id {RequestId})", requestId);
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Firma inválida", Extensions = { ["code"] = "invalid_signature" } });
        }

        if (dataId.Length == 0 || type.Length > 0 && !type.Equals("payment", StringComparison.OrdinalIgnoreCase))
        {
            // Otros eventos (merchant_order, etc.): se confirma recepcion y se ignoran.
            return Ok(new { received = true, ignored = true });
        }

        var payment = await mercadoPago.GetPaymentAsync(dataId, ct);
        if (payment is null)
        {
            // Pasa con las notificaciones de prueba del panel (id ficticio).
            logger.LogInformation("Webhook para un pago que Mercado Pago no conoce: {PaymentId}", dataId);
            return Ok(new { received = true, ignored = true });
        }

        await processor.ApplyAsync(MercadoPagoSignature.ToUpdate(payment), ct);
        return Ok(new { received = true });
    }

    /// <summary>
    /// SOLO desarrollo (Payments:Provider=Mock y Payments:AllowSimulation=true): empuja un estado de pago a un pedido
    /// para probar el flujo completo (approved, rejected, refunded...) sin Mercado Pago.
    /// </summary>
    [HttpPost("/api/dev/payments/simulate")]
    [AllowAnonymous]
    public async Task<ActionResult<OrderDto>> Simulate(SimulatePaymentRequest request, CancellationToken ct)
    {
        if (!(payments.Value.Provider == "Mock" && payments.Value.AllowSimulation && !env.IsProduction())) return NotFound();

        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.PublicId == request.OrderId, ct)
                    ?? throw AppException.NotFound("Pedido no encontrado.", "order_not_found");

        var update = new PaymentUpdate(
            "mock", $"sim-{Guid.NewGuid():N}"[..16], request.Status, null,
            request.Amount ?? order.Total, commerce.Value.Currency, "credit_card", order.Id.ToString());
        await processor.ApplyAsync(update, ct);

        var fresh = await db.Orders.AsNoTracking().Include(o => o.Items).FirstAsync(o => o.Id == order.Id, ct);
        return fresh.ToDto();
    }
}

[ApiController]
[Route("api")]
public class SupportController(AppDbContext db) : ControllerBase
{
    /// <summary>Formulario de contacto: queda guardado para que el equipo lo responda.</summary>
    [HttpPost("contact")]
    [EnableRateLimiting("public")]
    public async Task<IActionResult> Contact(ContactRequest request, CancellationToken ct)
    {
        db.ContactMessages.Add(new ContactMessage
        {
            Name = request.Name.Trim(),
            Email = request.Email.Trim().ToLowerInvariant(),
            OrderNumber = string.IsNullOrWhiteSpace(request.OrderNumber) ? null : request.OrderNumber.Trim().ToUpperInvariant(),
            Topic = request.Topic,
            Message = request.Message.Trim(),
        });
        await db.SaveChangesAsync(ct);
        return Accepted(new { ok = true });
    }

    /// <summary>Suscripcion al newsletter. Es idempotente y no revela si el email ya estaba suscripto.</summary>
    [HttpPost("newsletter")]
    [EnableRateLimiting("public")]
    public async Task<IActionResult> Newsletter(NewsletterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (!await db.NewsletterSubscribers.AnyAsync(s => s.Email == email, ct))
        {
            db.NewsletterSubscribers.Add(new NewsletterSubscriber { Email = email });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Dos suscripciones simultaneas: la otra gano, el resultado para el cliente es el mismo.
            }
        }

        return Accepted(new { ok = true });
    }
}
