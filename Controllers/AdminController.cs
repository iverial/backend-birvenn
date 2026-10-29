using BirvennApi.Dtos;
using BirvennApi.Models;
using BirvennApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirvennApi.Controllers;

/// <summary>Operacion interna del equipo. Solo usuarios con rol Admin (se siembra con Admin:Email / Admin:Password).</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Roles = Roles.Admin)]
public class AdminController(OrderService orders) : ControllerBase
{
    /// <summary>status acepta los valores del front (pendiente, pagado, preparando, enviado, entregado, cancelado).</summary>
    [HttpGet("orders")]
    public Task<PagedOrders> Orders(string? status, string? q, int page = 1, int pageSize = 20, CancellationToken ct = default) =>
        orders.PageAsync(ParseStatus(status), q, page, pageSize, ct);

    // El binding de enums por query string no usa los nombres JSON ("pendiente"), asi que se traduce a mano.
    private static OrderStatus? ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "pendiente" or "pending" => OrderStatus.Pending,
        "pagado" or "paid" => OrderStatus.Paid,
        "preparando" or "preparing" => OrderStatus.Preparing,
        "enviado" or "shipped" => OrderStatus.Shipped,
        "entregado" or "delivered" => OrderStatus.Delivered,
        "cancelado" or "cancelled" => OrderStatus.Cancelled,
        _ => throw Infrastructure.AppException.Unprocessable("Estado inválido. Usá pendiente, pagado, preparando, enviado, entregado o cancelado."),
    };

    /// <summary>Avanza un pedido: pagado → preparando → enviado → entregado (o cancelado). Acepta codigo de seguimiento.</summary>
    [HttpPatch("orders/{publicId}/status")]
    public async Task<ActionResult<OrderDto>> SetStatus(string publicId, UpdateOrderStatusRequest request, CancellationToken ct) =>
        (await orders.SetStatusAsync(publicId, request.Status, request.TrackingCode, ct)).ToDto();

    /// <summary>Marca como pagado un pedido por transferencia cuando el deposito ya llego.</summary>
    [HttpPost("orders/{publicId}/confirm-payment")]
    public async Task<ActionResult<OrderDto>> ConfirmPayment(string publicId, CancellationToken ct) =>
        (await orders.ConfirmTransferAsync(publicId, ct)).ToDto();

    /// <summary>Fija el stock de un talle de un producto.</summary>
    [HttpPut("products/{productId}/stock")]
    public async Task<IActionResult> SetStock(string productId, SetStockRequest request, CancellationToken ct)
    {
        await orders.SetStockAsync(productId, request.Size, request.Quantity, ct);
        return NoContent();
    }
}
