using BirvennApi.Models;
using BirvennApi.Options;
using Microsoft.Extensions.Options;

namespace BirvennApi.Payments;

public record GatewayResult(string Status, string? TransactionId, string? RedirectUrl, string? Message);

/// <summary>
/// Contrato de una pasarela (equivalente a `PaymentGateway` del front). Para sumar otra (Stripe, Ualá...):
/// implementar esta interfaz y registrarla en Program.cs segun `Payments:Provider`.
/// La transferencia bancaria no pasa por una pasarela: la resuelve CheckoutService.
/// </summary>
public interface IPaymentGateway
{
    string Id { get; }

    /// <summary>"approved" si ya se cobro, o "pending" si el cliente tiene que completar el pago (redirect).</summary>
    Task<GatewayResult> CreateCheckoutAsync(Order order, CancellationToken ct);
}

/// <summary>Pasarela simulada para desarrollo: aprueba tarjeta y Mercado Pago al instante, como el mock del front.</summary>
public class MockGateway : IPaymentGateway
{
    public string Id => "mock";

    public Task<GatewayResult> CreateCheckoutAsync(Order order, CancellationToken ct) =>
        Task.FromResult(new GatewayResult("approved", $"mock_{order.PublicId}", null, "Pago aprobado."));
}

/// <summary>Mercado Pago Checkout Pro: crea la preferencia y devuelve el init_point al que hay que redirigir.</summary>
public class MercadoPagoGateway(
    MercadoPagoClient client,
    IOptions<MercadoPagoOptions> mpOptions,
    IOptions<UrlsOptions> urlOptions,
    IOptions<CommerceOptions> commerceOptions) : IPaymentGateway
{
    private readonly MercadoPagoOptions _mp = mpOptions.Value;
    private readonly UrlsOptions _urls = urlOptions.Value;
    private readonly CommerceOptions _commerce = commerceOptions.Value;

    public string Id => "mercadopago";

    public async Task<GatewayResult> CreateCheckoutAsync(Order order, CancellationToken ct)
    {
        var front = _urls.Frontend.TrimEnd('/');
        var api = _urls.PublicApi.TrimEnd('/');
        var success = $"{front}/checkout/exito?orden={order.PublicId}";

        // Mercado Pago solo acepta auto_return y notification_url con HTTPS publico; en local se omiten
        // y el pago se confirma con /api/orders/{id}/reconcile al volver el cliente.
        var httpsFront = success.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var httpsApi = api.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        // Con tarjeta solo se ofrecen credito/debito; con Mercado Pago, todos los medios.
        var excluded = order.PaymentMethod == PaymentMethodId.Card
            ? new[] { "ticket", "atm", "bank_transfer", "account_money" }.Select(id => new MpIdRef(id)).ToList()
            : [];

        var request = new MpPreferenceRequest
        {
            // Un unico item con el total del pedido: es la forma de que lo cobrado coincida al centavo con
            // lo calculado (cupon, descuento por transferencia y envio no se pueden expresar como items).
            Items =
            [
                new MpItem
                {
                    Id = order.Number,
                    Title = $"BIRVENN — Pedido {order.Number}",
                    Description = Summarize(order),
                    Quantity = 1,
                    CurrencyId = _commerce.Currency,
                    UnitPrice = order.Total,
                },
            ],
            Payer = new MpPayer { Name = order.Address.FirstName, Surname = order.Address.LastName, Email = order.Email },
            ExternalReference = order.Id.ToString(),
            BackUrls = new MpBackUrls { Success = success, Pending = success, Failure = $"{front}/checkout?error=pago" },
            AutoReturn = httpsFront ? "approved" : null,
            NotificationUrl = httpsApi ? $"{api}/api/payments/webhook" : null,
            StatementDescriptor = _mp.StatementDescriptor,
            PaymentMethods = new MpPaymentMethods { ExcludedPaymentTypes = excluded, Installments = _commerce.Installments },
            Expires = true,
            ExpirationDateFrom = Iso(DateTime.UtcNow),
            ExpirationDateTo = Iso(order.ExpiresAt),
            Metadata = new Dictionary<string, string> { ["order_number"] = order.Number, ["order_public_id"] = order.PublicId },
        };

        var preference = await client.CreatePreferenceAsync(request, order.Id.ToString(), ct);
        var redirect = _mp.UseSandboxInitPoint && !string.IsNullOrEmpty(preference.SandboxInitPoint)
            ? preference.SandboxInitPoint
            : preference.InitPoint;

        return new GatewayResult("pending", preference.Id, redirect, null);
    }

    private static string Summarize(Order order)
    {
        var text = string.Join(", ", order.Items.Select(i => $"{i.Quantity}x {i.Name} ({i.ColorName}, {i.Size})"));
        return text.Length <= 250 ? text : text[..247] + "...";
    }

    // Mercado Pago pide ISO 8601 con milisegundos y offset. Se usa "Z" y no "+00:00": System.Text.Json escapa el "+"
    // en el JSON (secuencia u002B) y Mercado Pago no lo decodifica en las fechas (400 error_parsing_date).
    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
