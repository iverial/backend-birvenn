using System.Text.Json.Serialization;

namespace BirvennApi.Models;

// Los nombres JSON coinciden con los valores que ya usa el front ("pendiente", "estandar", "free-shipping"...).

public enum OrderStatus
{
    [JsonStringEnumMemberName("pendiente")] Pending,
    [JsonStringEnumMemberName("pagado")] Paid,
    [JsonStringEnumMemberName("preparando")] Preparing,
    [JsonStringEnumMemberName("enviado")] Shipped,
    [JsonStringEnumMemberName("entregado")] Delivered,
    [JsonStringEnumMemberName("cancelado")] Cancelled,
}

public enum ShippingMethodId
{
    [JsonStringEnumMemberName("estandar")] Standard,
    [JsonStringEnumMemberName("express")] Express,
    [JsonStringEnumMemberName("retiro")] Pickup,
}

public enum PaymentMethodId
{
    [JsonStringEnumMemberName("tarjeta")] Card,
    [JsonStringEnumMemberName("transferencia")] Transfer,
    [JsonStringEnumMemberName("mercadopago")] MercadoPago,
}

public enum CouponType
{
    [JsonStringEnumMemberName("percent")] Percent,
    [JsonStringEnumMemberName("fixed")] Fixed,
    [JsonStringEnumMemberName("free-shipping")] FreeShipping,
}

public enum ActivityType
{
    [JsonStringEnumMemberName("registro")] Registered,
    [JsonStringEnumMemberName("ingreso")] SignedIn,
    [JsonStringEnumMemberName("salida")] SignedOut,
    [JsonStringEnumMemberName("pedido")] OrderPlaced,
    [JsonStringEnumMemberName("cupon-aplicado")] CouponReceived,
    [JsonStringEnumMemberName("perfil-actualizado")] ProfileUpdated,
}

public static class Roles
{
    public const string Customer = "Customer";
    public const string Admin = "Admin";
}
