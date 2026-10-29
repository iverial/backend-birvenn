using System.Globalization;
using BirvennApi.Data;
using BirvennApi.Infrastructure;
using BirvennApi.Models;
using BirvennApi.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BirvennApi.Services;

public record CouponResult(Coupon? Coupon, string? Reason)
{
    public bool Valid => Coupon is not null;
}

public class CouponService(AppDbContext db, IOptions<CommerceOptions> options)
{
    private readonly CommerceOptions _commerce = options.Value;

    /// <summary>
    /// Validacion autoritativa. Los mensajes son los mismos que muestra el front, pero aca los cupones
    /// personales se comprueban contra la base: que existan, sean de ese usuario y no esten usados.
    /// </summary>
    public async Task<CouponResult> ValidateAsync(string? rawCode, decimal subtotal, Guid? userId, CancellationToken ct = default)
    {
        var code = (rawCode ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) return new(null, "Ingresá un código.");

        var coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code && c.Active, ct);
        if (coupon is null) return new(null, "El cupón no existe.");

        if (coupon.OwnerId is not null)
        {
            if (userId is null) return new(null, "Ingresá a tu cuenta para usar tu cupón de bienvenida.");
            if (coupon.OwnerId != userId) return new(null, "Este cupón no pertenece a tu cuenta.");
            if (coupon.UsedAt is not null) return new(null, "Ya usaste este cupón.");
        }

        if (coupon.ExpiresAt is not null && coupon.ExpiresAt < DateTime.UtcNow) return new(null, "El cupón está vencido.");

        if (coupon.MinSubtotal is { } min && subtotal < min) return new(null, $"Mínimo de compra {FormatPrice(min)}.");

        return new(coupon, null);
    }

    /// <summary>Crea (sin guardar) el cupon personal de bienvenida: 15% por 30 dias, configurable.</summary>
    public async Task<Coupon> CreateWelcomeAsync(Guid ownerId, CancellationToken ct = default)
    {
        string code;
        do
        {
            code = CodeGenerator.WelcomeCoupon();
        } while (await db.Coupons.AnyAsync(c => c.Code == code, ct));

        return new Coupon
        {
            Code = code,
            Type = CouponType.Percent,
            Value = _commerce.WelcomeCouponPercent,
            Description = $"{_commerce.WelcomeCouponPercent}% off en tu primera compra",
            ExpiresAt = DateTime.UtcNow.AddDays(_commerce.WelcomeCouponDays),
            OwnerId = ownerId,
        };
    }

    // "$ 60.000" sin depender de la cultura del servidor.
    private static string FormatPrice(decimal value) =>
        "$ " + ((long)value).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
}
