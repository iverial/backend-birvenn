namespace BirvennApi.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Siempre en minusculas.</summary>
    public string Email { get; set; } = "";

    public string PasswordHash { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Phone { get; set; }
    public DateOnly? Birthday { get; set; }
    public bool Newsletter { get; set; }
    public string Role { get; set; } = Roles.Customer;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int FailedLogins { get; set; }
    public DateTime? LockoutEnd { get; set; }

    public List<Coupon> Coupons { get; set; } = [];
    public List<ActivityEntry> Activity { get; set; } = [];
}

public class ActivityEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public ActivityType Type { get; set; }
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Category
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Tone { get; set; } = "";
    public int SortOrder { get; set; }
}

public class Color
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hex { get; set; } = "";
}

public class Product
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public string CategorySlug { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Graphic { get; set; } = "none";
    public decimal Price { get; set; }
    public decimal? CompareAtPrice { get; set; }
    public string Fit { get; set; } = "";
    public string Collection { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string Description { get; set; } = "";
    public string Materials { get; set; } = "";
    public decimal Rating { get; set; }
    public int ReviewsCount { get; set; }
    public DateOnly CreatedAt { get; set; }
    public bool Active { get; set; } = true;

    public List<string> Sizes { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Details { get; set; } = [];
    public List<string> Care { get; set; } = [];
    public List<string> Images { get; set; } = [];

    public List<ProductColorLink> ColorLinks { get; set; } = [];
    public List<ProductStock> Stock { get; set; } = [];
}

/// <summary>Colores de un producto, en el orden en que se muestran.</summary>
public class ProductColorLink
{
    public string ProductId { get; set; } = "";
    public string ColorId { get; set; } = "";
    public int Position { get; set; }
    public Color Color { get; set; } = null!;
}

/// <summary>El stock es por talle (el color no tiene stock propio), igual que en el front.</summary>
public class ProductStock
{
    public string ProductId { get; set; } = "";
    public string Size { get; set; } = "";
    public int Quantity { get; set; }
}

public class Coupon
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Siempre en mayusculas.</summary>
    public string Code { get; set; } = "";

    public CouponType Type { get; set; }
    public decimal Value { get; set; }
    public string Description { get; set; } = "";
    public decimal? MinSubtotal { get; set; }
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Si tiene dueno es un cupon personal (ej. el de bienvenida).</summary>
    public Guid? OwnerId { get; set; }

    public DateTime? UsedAt { get; set; }
    public Guid? UsedByOrderId { get; set; }
    public bool Active { get; set; } = true;
}

public class ShippingAddress
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Street { get; set; } = "";
    public string Number { get; set; } = "";
    public string? Apartment { get; set; }
    public string City { get; set; } = "";
    public string Province { get; set; } = "";
    public string Zip { get; set; } = "";
    public string? Notes { get; set; }
}

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Id publico e impredecible. Es el que viaja en las URLs y funciona como "llave" para pedidos de invitado.</summary>
    public string PublicId { get; set; } = "";

    /// <summary>Numero legible para el cliente (BRV-XXXXXX).</summary>
    public string Number { get; set; } = "";

    /// <summary>Clave de idempotencia que manda el front para que un doble click no genere dos pedidos.</summary>
    public string? ClientOrderId { get; set; }

    public Guid? UserId { get; set; }
    public string Email { get; set; } = "";
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public ShippingMethodId ShippingMethod { get; set; }
    public PaymentMethodId PaymentMethod { get; set; }

    public string? CouponCode { get; set; }

    public decimal Subtotal { get; set; }
    public decimal CouponDiscount { get; set; }
    public decimal PaymentDiscount { get; set; }
    public decimal Shipping { get; set; }
    public decimal Total { get; set; }

    public ShippingAddress Address { get; set; } = new();

    public string PaymentProvider { get; set; } = "";

    /// <summary>Id de preferencia (Mercado Pago) o de transaccion, segun el proveedor.</summary>
    public string? TransactionId { get; set; }

    /// <summary>init_point de Checkout Pro, para poder reanudar el pago.</summary>
    public string? PaymentRedirectUrl { get; set; }

    public string? PaymentNote { get; set; }
    public string? TrackingCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public List<OrderItem> Items { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
}

public class OrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string LineId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public string ColorId { get; set; } = "";
    public string ColorName { get; set; } = "";
    public string ColorHex { get; set; } = "";
    public string Size { get; set; } = "";
    public int Quantity { get; set; }
    public string Kind { get; set; } = "";
    public string Graphic { get; set; } = "";
}

/// <summary>Cada intento de pago que informa la pasarela (un pedido puede tener varios).</summary>
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Provider { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? StatusDetail { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ARS";
    public string? MethodType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class ContactMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? OrderNumber { get; set; }
    public string Topic { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Handled { get; set; }
}

public class NewsletterSubscriber
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;
}
