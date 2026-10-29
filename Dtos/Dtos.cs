using System.ComponentModel.DataAnnotations;
using BirvennApi.Infrastructure;
using BirvennApi.Models;

namespace BirvennApi.Dtos;

// ---------- Auth y cuenta (mismas formas que los tipos User/Coupon/ActivityEntry del front) ----------

public class RegisterRequest
{
    [Required(ErrorMessage = "Requerido"), StringLength(40, MinimumLength = 2, ErrorMessage = "Entre 2 y 40 caracteres"), RegularExpression(Rules.PersonName, ErrorMessage = "Solo letras")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Requerido"), StringLength(40, MinimumLength = 2, ErrorMessage = "Entre 2 y 40 caracteres"), RegularExpression(Rules.PersonName, ErrorMessage = "Solo letras")]
    public string LastName { get; set; } = "";

    [Required(ErrorMessage = "Ingresá un email válido"), EmailAddress(ErrorMessage = "Ingresá un email válido"), StringLength(120, ErrorMessage = "Email demasiado largo")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Ingresá una contraseña"), PasswordRules]
    public string Password { get; set; } = "";

    public bool Newsletter { get; set; }

    [MustBeTrue]
    public bool AcceptTerms { get; set; }
}

public class LoginRequest
{
    [Required(ErrorMessage = "Ingresá un email válido"), EmailAddress(ErrorMessage = "Ingresá un email válido"), StringLength(120)]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Ingresá tu contraseña"), StringLength(200)]
    public string Password { get; set; } = "";

    public bool Remember { get; set; }
}

public class UpdateProfileRequest
{
    [Required(ErrorMessage = "Requerido"), StringLength(40, MinimumLength = 2, ErrorMessage = "Entre 2 y 40 caracteres"), RegularExpression(Rules.PersonName, ErrorMessage = "Solo letras")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Requerido"), StringLength(40, MinimumLength = 2, ErrorMessage = "Entre 2 y 40 caracteres"), RegularExpression(Rules.PersonName, ErrorMessage = "Solo letras")]
    public string LastName { get; set; } = "";

    [RegularExpression(Rules.PhoneOptional, ErrorMessage = "Teléfono inválido")]
    public string? Phone { get; set; }

    /// <summary>yyyy-MM-dd o vacio.</summary>
    [StringLength(10)]
    public string? Birthday { get; set; }

    public bool Newsletter { get; set; }
}

public record CouponDto(
    string Code,
    CouponType Type,
    decimal Value,
    string Description,
    decimal? MinSubtotal,
    DateTime? ExpiresAt,
    Guid? OwnerId,
    DateTime? UsedAt);

public record ActivityDto(Guid Id, ActivityType Type, string Message, DateTime CreatedAt);

public record UserDto(
    Guid Id,
    string FirstName,
    string LastName,
    string Email,
    string? Phone,
    string? Birthday,
    bool Newsletter,
    string Role,
    DateTime CreatedAt,
    List<CouponDto> Coupons,
    List<ActivityDto> Activity);

public record AuthResponse(UserDto User, bool IsNew);

// ---------- Catalogo ----------

public record ColorDto(string Id, string Name, string Hex);

public record ProductDto(
    string Id,
    string Slug,
    string Name,
    string Category,
    string Kind,
    string Graphic,
    decimal Price,
    decimal? CompareAtPrice,
    List<ColorDto> Colors,
    List<string> Sizes,
    Dictionary<string, int> Stock,
    List<string> Tags,
    string Fit,
    string Collection,
    string ShortDescription,
    string Description,
    List<string> Details,
    string Materials,
    List<string> Care,
    decimal Rating,
    int ReviewsCount,
    List<string>? Images,
    string CreatedAt);

public record CategoryDto(string Slug, string Name, string Description, string Kind, string Tone);

public record ProductListResponse(List<ProductDto> Items, int Total, decimal PriceCeiling);

// ---------- Cupones, presupuesto y checkout ----------

public class ValidateCouponRequest
{
    [Required(ErrorMessage = "Ingresá un código."), StringLength(40)]
    public string Code { get; set; } = "";

    [Range(0, 100_000_000)]
    public decimal Subtotal { get; set; }
}

public record CouponValidationResponse(bool Valid, CouponDto? Coupon, string? Reason);

public class CheckoutItemRequest
{
    [Required, StringLength(32, MinimumLength = 1)]
    public string ProductId { get; set; } = "";

    [Required, StringLength(32, MinimumLength = 1)]
    public string ColorId { get; set; } = "";

    [Required, OneOf("XS", "S", "M", "L", "XL", "XXL", "U", ErrorMessage = "Talle inválido")]
    public string Size { get; set; } = "";

    [Range(1, 100, ErrorMessage = "Cantidad inválida")]
    public int Quantity { get; set; } = 1;
}

/// <summary>Lo unico que el cliente decide: que prendas, que cupon y como quiere recibir y pagar. Los precios los pone el servidor.</summary>
public class QuoteRequest
{
    [Required(ErrorMessage = "Tu carrito está vacío."), MinLength(1, ErrorMessage = "Tu carrito está vacío."), MaxLength(50)]
    public List<CheckoutItemRequest> Items { get; set; } = [];

    [StringLength(40)]
    public string? CouponCode { get; set; }

    public ShippingMethodId ShippingMethod { get; set; } = ShippingMethodId.Standard;

    public PaymentMethodId PaymentMethod { get; set; } = PaymentMethodId.Card;
}

public class CheckoutRequest : QuoteRequest
{
    /// <summary>Id que genera el front por intento de checkout. Si se repite, se devuelve el mismo pedido en vez de crear otro.</summary>
    [StringLength(64)]
    public string? ClientOrderId { get; set; }

    [Required(ErrorMessage = "Ingresá un email válido"), EmailAddress(ErrorMessage = "Ingresá un email válido"), StringLength(120)]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Requerido"), StringLength(40, MinimumLength = 2, ErrorMessage = "Entre 2 y 40 caracteres")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Requerido"), StringLength(40, MinimumLength = 2, ErrorMessage = "Entre 2 y 40 caracteres")]
    public string LastName { get; set; } = "";

    [Required(ErrorMessage = "Teléfono inválido"), RegularExpression(Rules.Phone, ErrorMessage = "Teléfono inválido")]
    public string Phone { get; set; } = "";

    [Required(ErrorMessage = "Requerido"), StringLength(80, MinimumLength = 2, ErrorMessage = "Entre 2 y 80 caracteres")]
    public string Street { get; set; } = "";

    [Required(ErrorMessage = "Requerido"), StringLength(10, MinimumLength = 1, ErrorMessage = "Hasta 10 caracteres")]
    public string Number { get; set; } = "";

    [StringLength(20, ErrorMessage = "Hasta 20 caracteres")]
    public string? Apartment { get; set; }

    [Required(ErrorMessage = "Requerido"), StringLength(60, MinimumLength = 2, ErrorMessage = "Entre 2 y 60 caracteres")]
    public string City { get; set; } = "";

    [Required(ErrorMessage = "Elegí una provincia")]
    public string Province { get; set; } = "";

    [Required(ErrorMessage = "Código postal inválido"), RegularExpression(Rules.Zip, ErrorMessage = "Código postal inválido")]
    public string Zip { get; set; } = "";

    [StringLength(200, ErrorMessage = "Hasta 200 caracteres")]
    public string? Notes { get; set; }

    [MustBeTrue]
    public bool AcceptTerms { get; set; }
}

public record TotalsDto(decimal Subtotal, decimal CouponDiscount, decimal PaymentDiscount, decimal Shipping, decimal Total);

public record QuoteResponse(TotalsDto Totals, CouponDto? Coupon, string? CouponError, decimal RemainingForFreeShipping);

public record PaymentOutcomeDto(string Status, string? RedirectUrl, string? Message, string? TransactionId);

public record CheckoutResponse(OrderDto Order, PaymentOutcomeDto Payment);

// ---------- Pedidos ----------

public record AddressDto(
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string Street,
    string Number,
    string? Apartment,
    string City,
    string Province,
    string Zip,
    string? Notes);

public record OrderItemDto(
    string LineId,
    string ProductId,
    string Slug,
    string Name,
    decimal UnitPrice,
    string ColorId,
    string ColorName,
    string ColorHex,
    string Size,
    int Quantity,
    string Kind,
    string Graphic);

public record OrderDto(
    string Id,
    string Number,
    DateTime CreatedAt,
    OrderStatus Status,
    Guid? UserId,
    string Email,
    List<OrderItemDto> Items,
    AddressDto Address,
    ShippingMethodId ShippingMethod,
    PaymentMethodId PaymentMethod,
    string? CouponCode,
    TotalsDto Totals,
    string? TransactionId,
    string? TrackingCode,
    DateTime? PaidAt,
    DateTime ExpiresAt);

public class ReconcileRequest
{
    /// <summary>payment_id / collection_id que Mercado Pago agrega a la back_url. Se verifica contra su API, nunca se confia.</summary>
    [StringLength(40)]
    public string? PaymentId { get; set; }
}

// ---------- Soporte ----------

public class ContactRequest
{
    [Required(ErrorMessage = "Requerido"), StringLength(60, MinimumLength = 2, ErrorMessage = "Entre 2 y 60 caracteres")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Ingresá un email válido"), EmailAddress(ErrorMessage = "Ingresá un email válido"), StringLength(120)]
    public string Email { get; set; } = "";

    [StringLength(20)]
    public string? OrderNumber { get; set; }

    [Required(ErrorMessage = "Elegí un motivo"), OneOf("pedido", "cambio", "talles", "mayorista", "otro", ErrorMessage = "Elegí un motivo")]
    public string Topic { get; set; } = "";

    [Required(ErrorMessage = "Contanos un poco más (mín. 10 caracteres)"), StringLength(1000, MinimumLength = 10, ErrorMessage = "Contanos un poco más (mín. 10 caracteres)")]
    public string Message { get; set; } = "";
}

public class NewsletterRequest
{
    [Required(ErrorMessage = "Ingresá un email válido"), EmailAddress(ErrorMessage = "Ingresá un email válido"), StringLength(120)]
    public string Email { get; set; } = "";
}

// ---------- Configuracion publica y admin ----------

public record ShippingMethodDto(ShippingMethodId Id, string Name, string Description, string Eta, decimal Price, bool FreeOverThreshold);

public record PaymentMethodDto(PaymentMethodId Id, string Name, string Description);

public record PublicConfigDto(
    string Currency,
    decimal FreeShippingThreshold,
    int Installments,
    decimal TransferDiscount,
    int WelcomeCouponPercent,
    int MaxQuantityPerItem,
    List<ShippingMethodDto> ShippingMethods,
    List<PaymentMethodDto> PaymentMethods,
    string[] Provinces,
    string PaymentProvider);

public class UpdateOrderStatusRequest
{
    public OrderStatus Status { get; set; }

    [StringLength(60)]
    public string? TrackingCode { get; set; }
}

public class SetStockRequest
{
    [Required, OneOf("XS", "S", "M", "L", "XL", "XXL", "U", ErrorMessage = "Talle inválido")]
    public string Size { get; set; } = "";

    [Range(0, 100_000)]
    public int Quantity { get; set; }
}

public record PagedOrders(List<OrderDto> Items, int Total, int Page, int PageSize);

public class SimulatePaymentRequest
{
    [Required]
    public string OrderId { get; set; } = "";

    /// <summary>approved, pending, in_process, rejected, cancelled, refunded, charged_back.</summary>
    [Required]
    public string Status { get; set; } = "approved";

    /// <summary>Si no se manda, usa el total del pedido.</summary>
    public decimal? Amount { get; set; }
}
