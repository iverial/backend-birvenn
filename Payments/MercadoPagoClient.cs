using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirvennApi.Infrastructure;

namespace BirvennApi.Payments;

// ---------- Modelos de la API de Mercado Pago (JSON en snake_case) ----------

public class MpPreferenceRequest
{
    public List<MpItem> Items { get; set; } = [];
    public MpPayer Payer { get; set; } = new();
    public string ExternalReference { get; set; } = "";
    public MpBackUrls BackUrls { get; set; } = new();
    public string? AutoReturn { get; set; }
    public string? NotificationUrl { get; set; }
    public string StatementDescriptor { get; set; } = "";
    public MpPaymentMethods PaymentMethods { get; set; } = new();
    public bool Expires { get; set; }
    public string? ExpirationDateFrom { get; set; }
    public string? ExpirationDateTo { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = [];
}

public class MpItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int Quantity { get; set; }
    public string CurrencyId { get; set; } = "ARS";
    public decimal UnitPrice { get; set; }
}

public class MpPayer
{
    public string Name { get; set; } = "";
    public string Surname { get; set; } = "";
    public string Email { get; set; } = "";
}

public class MpBackUrls
{
    public string Success { get; set; } = "";
    public string Failure { get; set; } = "";
    public string Pending { get; set; } = "";
}

public class MpPaymentMethods
{
    public List<MpIdRef> ExcludedPaymentTypes { get; set; } = [];
    public int? Installments { get; set; }
}

public record MpIdRef(string Id);

public record MpPreference(string Id, string InitPoint, string? SandboxInitPoint);

public record MpPayment(
    long Id,
    string Status,
    string? StatusDetail,
    string? ExternalReference,
    decimal? TransactionAmount,
    string? CurrencyId,
    string? PaymentTypeId,
    string? PaymentMethodId);

public record MpSearchResult(List<MpPayment> Results);

/// <summary>Cliente HTTP minimo de Mercado Pago: crear preferencia, leer un pago y buscar pagos de un pedido.</summary>
public class MercadoPagoClient(HttpClient http, ILogger<MercadoPagoClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<MpPreference> CreatePreferenceAsync(MpPreferenceRequest body, string idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "checkout/preferences")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Add("X-Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, "crear la preferencia", ct);

        return await response.Content.ReadFromJsonAsync<MpPreference>(Json, ct)
               ?? throw new AppException(502, "gateway_error", "No pudimos procesar el pago. Intentá nuevamente.");
    }

    public async Task<MpPayment?> GetPaymentAsync(string paymentId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"v1/payments/{Uri.EscapeDataString(paymentId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, "leer el pago", ct);

        return await response.Content.ReadFromJsonAsync<MpPayment>(Json, ct);
    }

    public async Task<List<MpPayment>> SearchPaymentsAsync(string externalReference, CancellationToken ct)
    {
        var url = $"v1/payments/search?external_reference={Uri.EscapeDataString(externalReference)}&sort=date_created&criteria=desc";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) throw await FailureAsync(response, "buscar pagos", ct);

        var result = await response.Content.ReadFromJsonAsync<MpSearchResult>(Json, ct);
        return result?.Results ?? [];
    }

    private async Task<AppException> FailureAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        // Se loguea el cuerpo del error (sin credenciales) para poder diagnosticar; al cliente solo le llega un mensaje generico.
        var body = await response.Content.ReadAsStringAsync(ct);
        logger.LogError("Mercado Pago: fallo al {Action}. HTTP {Status}. {Body}", action, (int)response.StatusCode,
            body.Length > 600 ? body[..600] : body);
        return new AppException(502, "gateway_error", "No pudimos procesar el pago. Intentá nuevamente.");
    }
}

/// <summary>
/// Verifica la firma de las notificaciones (header x-signature).
/// Mercado Pago firma con HMAC-SHA256 el texto "id:{data.id};request-id:{x-request-id};ts:{ts};" usando la
/// clave secreta de Webhooks. Los campos que no vienen se omiten del texto.
/// </summary>
public static class MercadoPagoSignature
{
    public static bool Verify(string? signatureHeader, string? requestId, string? dataId, string secret)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(secret)) return false;

        string? ts = null, v1 = null;
        foreach (var part in signatureHeader.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            var (key, value) = (pair[0].Trim(), pair[1].Trim());
            if (key == "ts") ts = value;
            else if (key == "v1") v1 = value;
        }

        if (ts is null || v1 is null) return false;

        var manifest = new StringBuilder();
        if (!string.IsNullOrEmpty(dataId))
        {
            // Segun la documentacion, si el id es alfanumerico va en minusculas.
            manifest.Append("id:").Append(dataId.ToLowerInvariant()).Append(';');
        }

        if (!string.IsNullOrEmpty(requestId)) manifest.Append("request-id:").Append(requestId).Append(';');
        manifest.Append("ts:").Append(ts).Append(';');

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest.ToString())));

        // Comparacion en tiempo constante.
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(v1.ToLowerInvariant()));
    }

    public static PaymentUpdate ToUpdate(MpPayment p) =>
        new("mercadopago", p.Id.ToString(), p.Status, p.StatusDetail, p.TransactionAmount, p.CurrencyId, p.PaymentTypeId, p.ExternalReference ?? "");
}
