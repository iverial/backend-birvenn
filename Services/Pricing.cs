using BirvennApi.Models;
using BirvennApi.Options;
using Microsoft.Extensions.Options;

namespace BirvennApi.Services;

public record PricingLine(decimal UnitPrice, int Quantity);

public record Totals(decimal Subtotal, decimal CouponDiscount, decimal PaymentDiscount, decimal Shipping, decimal Total);

public record ShippingMethodInfo(ShippingMethodId Id, string Name, string Description, string Eta, decimal Price, bool FreeOverThreshold);

public record PaymentMethodInfo(PaymentMethodId Id, string Name, string Description);

/// <summary>Mismos metodos y precios que `lib/data/checkout.ts` del front.</summary>
public static class ShippingCatalog
{
    public static readonly IReadOnlyList<ShippingMethodInfo> Methods =
    [
        new(ShippingMethodId.Standard, "Envío estándar", "A domicilio en todo el país.", "3 a 6 días hábiles", 6_500, true),
        new(ShippingMethodId.Express, "Express 24/48 h", "CABA y GBA.", "24 a 48 h hábiles", 11_900, false),
        new(ShippingMethodId.Pickup, "Retiro en showroom", "Palermo, CABA.", "Listo en 24 h", 0, false),
    ];

    public static ShippingMethodInfo Get(ShippingMethodId id) => Methods.First(m => m.Id == id);

    public static IReadOnlyList<PaymentMethodInfo> PaymentMethods(CommerceOptions c) =>
    [
        new(PaymentMethodId.Card, "Tarjeta de crédito o débito", $"Hasta {c.Installments} cuotas sin interés."),
        new(PaymentMethodId.Transfer, "Transferencia bancaria", $"{c.TransferDiscount * 100:0}% de descuento extra."),
        new(PaymentMethodId.MercadoPago, "Mercado Pago", "Dinero en cuenta, QR o tarjetas guardadas."),
    ];
}

/// <summary>
/// Calculo unico de totales: port exacto de `calculateTotals` (lib/pricing.ts). El monto que se cobra
/// sale siempre de aca, nunca del navegador.
/// </summary>
public class PricingService(IOptions<CommerceOptions> options)
{
    private readonly CommerceOptions _commerce = options.Value;

    public Totals Calculate(
        IReadOnlyCollection<PricingLine> lines,
        Coupon? coupon,
        ShippingMethodId shippingMethod,
        PaymentMethodId paymentMethod)
    {
        var subtotal = lines.Sum(l => l.UnitPrice * l.Quantity);
        var validCoupon = coupon is not null && subtotal >= (coupon.MinSubtotal ?? 0) ? coupon : null;

        decimal couponDiscount = 0;
        if (validCoupon?.Type == CouponType.Percent) couponDiscount = subtotal * validCoupon.Value / 100;
        if (validCoupon?.Type == CouponType.Fixed) couponDiscount = Math.Min(validCoupon.Value, subtotal);

        var method = ShippingCatalog.Get(shippingMethod);
        var qualifiesFree =
            (method.FreeOverThreshold && subtotal >= _commerce.FreeShippingThreshold) ||
            (validCoupon?.Type == CouponType.FreeShipping && method.Id == ShippingMethodId.Standard);
        var shipping = subtotal == 0 || qualifiesFree ? 0 : method.Price;

        var afterCoupon = subtotal - couponDiscount;
        var paymentDiscount = paymentMethod == PaymentMethodId.Transfer ? afterCoupon * _commerce.TransferDiscount : 0;

        var total = Math.Max(0, afterCoupon - paymentDiscount + shipping);

        return new Totals(Round(subtotal), Round(couponDiscount), Round(paymentDiscount), Round(shipping), Round(total));
    }

    public decimal RemainingForFreeShipping(decimal subtotal) => Math.Max(0, _commerce.FreeShippingThreshold - subtotal);

    // JavaScript redondea .5 hacia arriba; el redondeo bancario de .NET daria 1 peso de diferencia con el front.
    private static decimal Round(decimal value) => Math.Round(value, 0, MidpointRounding.AwayFromZero);
}
