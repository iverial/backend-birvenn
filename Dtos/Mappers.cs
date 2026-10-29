using BirvennApi.Models;

namespace BirvennApi.Dtos;

public static class Mappers
{
    public static CouponDto ToDto(this Coupon c) =>
        new(c.Code, c.Type, c.Value, c.Description, c.MinSubtotal, c.ExpiresAt, c.OwnerId, c.UsedAt);

    public static ActivityDto ToDto(this ActivityEntry a) => new(a.Id, a.Type, a.Message, a.CreatedAt);

    /// <summary>Requiere Coupons y Activity cargados.</summary>
    public static UserDto ToDto(this User u) => new(
        u.Id,
        u.FirstName,
        u.LastName,
        u.Email,
        u.Phone,
        u.Birthday?.ToString("yyyy-MM-dd"),
        u.Newsletter,
        u.Role,
        u.CreatedAt,
        [.. u.Coupons.OrderByDescending(c => c.ExpiresAt ?? DateTime.MaxValue).Select(c => c.ToDto())],
        [.. u.Activity.OrderByDescending(a => a.CreatedAt).Select(a => a.ToDto())]);

    /// <summary>Requiere ColorLinks (con Color) y Stock cargados.</summary>
    public static ProductDto ToDto(this Product p) => new(
        p.Id,
        p.Slug,
        p.Name,
        p.CategorySlug,
        p.Kind,
        p.Graphic,
        p.Price,
        p.CompareAtPrice,
        [.. p.ColorLinks.OrderBy(l => l.Position).Select(l => new ColorDto(l.Color.Id, l.Color.Name, l.Color.Hex))],
        [.. p.Sizes],
        // Talle sin fila o con 0 = sin stock (igual que `stock[size] ?? 0` en el front).
        p.Sizes.ToDictionary(s => s, s => p.Stock.FirstOrDefault(x => x.Size == s)?.Quantity ?? 0),
        [.. p.Tags],
        p.Fit,
        p.Collection,
        p.ShortDescription,
        p.Description,
        [.. p.Details],
        p.Materials,
        [.. p.Care],
        p.Rating,
        p.ReviewsCount,
        p.Images.Count > 0 ? [.. p.Images] : null,
        p.CreatedAt.ToString("yyyy-MM-dd"));

    public static CategoryDto ToDto(this Category c) => new(c.Slug, c.Name, c.Description, c.Kind, c.Tone);

    public static TotalsDto ToDto(this Services.Totals t) =>
        new(t.Subtotal, t.CouponDiscount, t.PaymentDiscount, t.Shipping, t.Total);

    /// <summary>Requiere Items cargados.</summary>
    public static OrderDto ToDto(this Order o) => new(
        o.PublicId,
        o.Number,
        o.CreatedAt,
        o.Status,
        o.UserId,
        o.Email,
        [.. o.Items.Select(i => new OrderItemDto(
            i.LineId, i.ProductId, i.Slug, i.Name, i.UnitPrice, i.ColorId, i.ColorName, i.ColorHex,
            i.Size, i.Quantity, i.Kind, i.Graphic))],
        new AddressDto(
            o.Address.FirstName, o.Address.LastName, o.Address.Email, o.Address.Phone, o.Address.Street,
            o.Address.Number, o.Address.Apartment, o.Address.City, o.Address.Province, o.Address.Zip, o.Address.Notes),
        o.ShippingMethod,
        o.PaymentMethod,
        o.CouponCode,
        new TotalsDto(o.Subtotal, o.CouponDiscount, o.PaymentDiscount, o.Shipping, o.Total),
        o.TransactionId,
        o.TrackingCode,
        o.PaidAt,
        o.ExpiresAt);
}
