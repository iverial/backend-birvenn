using BirvennApi.Dtos;
using BirvennApi.Infrastructure;
using BirvennApi.Options;
using BirvennApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace BirvennApi.Controllers;

[ApiController]
[Route("api")]
public class CatalogController(CatalogService catalog) : ControllerBase
{
    /// <summary>
    /// Listado con los mismos filtros que la tienda del front. sizes/colors/tags van separados por coma.
    /// sort: relevancia | nuevos | precio-asc | precio-desc | mas-vendidos.
    /// </summary>
    [HttpGet("products")]
    public Task<ProductListResponse> List(
        string? category, string? sizes, string? colors, string? tags, decimal? priceMax, string? q,
        string sort = "relevancia", int limit = 100, int offset = 0, CancellationToken ct = default) =>
        catalog.ListAsync(
            new ProductQuery(
                string.IsNullOrWhiteSpace(category) ? null : category,
                Split(sizes), Split(colors), Split(tags), priceMax, q, sort,
                Math.Clamp(limit, 1, 100), Math.Max(0, offset)),
            ct);

    /// <summary>Busqueda rapida del buscador del header (sin tildes, con limite).</summary>
    [HttpGet("products/search")]
    public Task<List<ProductDto>> Search(string q = "", int limit = 6, CancellationToken ct = default) =>
        catalog.SearchAsync(q, Math.Clamp(limit, 1, 20), ct);

    /// <summary>Por id (p-001) o por slug (hoodie-flight-arena).</summary>
    [HttpGet("products/{idOrSlug}")]
    public async Task<ActionResult<ProductDto>> Get(string idOrSlug, CancellationToken ct) =>
        await catalog.GetAsync(idOrSlug, ct) ?? throw AppException.NotFound("Producto no encontrado.", "product_not_found");

    [HttpGet("categories")]
    public Task<List<CategoryDto>> Categories(CancellationToken ct) => catalog.CategoriesAsync(ct);

    private static string[] Split(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

[ApiController]
[Route("api/config")]
public class ConfigController(IOptions<CommerceOptions> commerce, IOptions<PaymentsOptions> payments) : ControllerBase
{
    /// <summary>Reglas comerciales y metodos de envio/pago, para que el front no las duplique.</summary>
    [HttpGet]
    public PublicConfigDto Get()
    {
        var c = commerce.Value;
        return new PublicConfigDto(
            c.Currency,
            c.FreeShippingThreshold,
            c.Installments,
            c.TransferDiscount,
            c.WelcomeCouponPercent,
            c.MaxQuantityPerItem,
            [.. ShippingCatalog.Methods.Select(m => new ShippingMethodDto(m.Id, m.Name, m.Description, m.Eta, m.Price, m.FreeOverThreshold))],
            [.. ShippingCatalog.PaymentMethods(c).Select(m => new PaymentMethodDto(m.Id, m.Name, m.Description))],
            Rules.Provinces,
            payments.Value.Provider);
    }
}

[ApiController]
[Route("api/coupons")]
public class CouponsController(CouponService coupons) : ControllerBase
{
    /// <summary>Valida un cupon para el carrito (con el usuario de la sesion, si hay). Reemplaza `validateCoupon` del front.</summary>
    [HttpPost("validate")]
    [EnableRateLimiting("public")]
    public async Task<ActionResult<CouponValidationResponse>> Validate(ValidateCouponRequest request, CancellationToken ct)
    {
        var result = await coupons.ValidateAsync(request.Code, request.Subtotal, User.GetUserId(), ct);
        return new CouponValidationResponse(result.Valid, result.Coupon?.ToDto(), result.Reason);
    }
}

[ApiController]
[Route("api/checkout")]
public class CheckoutController(CheckoutService checkout) : ControllerBase
{
    /// <summary>Totales calculados por el servidor para el carrito actual (no crea nada).</summary>
    [HttpPost("quote")]
    [EnableRateLimiting("public")]
    public Task<QuoteResponse> Quote(QuoteRequest request, CancellationToken ct) =>
        checkout.QuoteAsync(request, User.GetUserId(), ct);

    /// <summary>
    /// Crea el pedido, reserva el stock y arranca el pago. Con Mercado Pago devuelve payment.redirectUrl
    /// (Checkout Pro) al que hay que redirigir al cliente. Funciona como invitado o con sesion.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("checkout")]
    [ProducesResponseType<CheckoutResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(CheckoutRequest request, CancellationToken ct)
    {
        var result = await checkout.CreateAsync(request, User.GetUserId(), ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }
}

[ApiController]
[Route("api/orders")]
public class OrdersController(OrderService orders) : ControllerBase
{
    /// <summary>Historial del usuario logueado (reemplaza `orderService.getByUser`).</summary>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<List<OrderDto>>> Mine(CancellationToken ct) =>
        (await orders.ListForUserAsync(User.GetUserId()!.Value, ct)).Select(o => o.ToDto()).ToList();

    /// <summary>Un pedido por su id publico (el de la URL /checkout/exito?orden=...).</summary>
    [HttpGet("{publicId}")]
    [EnableRateLimiting("public")]
    public async Task<ActionResult<OrderDto>> Get(string publicId, CancellationToken ct) =>
        (await orders.GetByPublicIdAsync(publicId, ct)).ToDto();

    /// <summary>
    /// Confirma contra Mercado Pago el pago de un pedido. El front lo llama al volver de Checkout Pro
    /// (con el payment_id de la URL, si viene) para no depender de que el webhook haya llegado antes.
    /// </summary>
    [HttpPost("{publicId}/reconcile")]
    [EnableRateLimiting("public")]
    public async Task<ActionResult<OrderDto>> Reconcile(string publicId, ReconcileRequest? request, CancellationToken ct) =>
        (await orders.ReconcileAsync(publicId, request?.PaymentId, ct)).ToDto();
}
