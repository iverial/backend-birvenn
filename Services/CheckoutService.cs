using BirvennApi.Data;
using BirvennApi.Dtos;
using BirvennApi.Infrastructure;
using BirvennApi.Models;
using BirvennApi.Options;
using BirvennApi.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BirvennApi.Services;

/// <summary>
/// Reemplaza el route handler `/api/payments` y `submitCheckout` del front. El cliente solo manda ids,
/// talles y cantidades: precios, stock, cupon y totales se resuelven siempre aca.
/// </summary>
public class CheckoutService(
    AppDbContext db,
    PricingService pricing,
    CouponService coupons,
    OrderLifecycle lifecycle,
    IPaymentGateway gateway,
    IOptions<CommerceOptions> commerceOptions,
    ILogger<CheckoutService> logger)
{
    private readonly CommerceOptions _commerce = commerceOptions.Value;

    private sealed record Line(Product Product, Color Color, string Size, int Quantity);

    public async Task<QuoteResponse> QuoteAsync(QuoteRequest request, Guid? userId, CancellationToken ct)
    {
        var lines = await ResolveLinesAsync(request.Items, checkStock: false, ct);
        var pricingLines = lines.Select(l => new PricingLine(l.Product.Price, l.Quantity)).ToList();
        var subtotal = pricingLines.Sum(l => l.UnitPrice * l.Quantity);

        CouponResult? coupon = null;
        if (!string.IsNullOrWhiteSpace(request.CouponCode))
            coupon = await coupons.ValidateAsync(request.CouponCode, subtotal, userId, ct);

        var totals = pricing.Calculate(pricingLines, coupon?.Coupon, request.ShippingMethod, request.PaymentMethod);
        return new QuoteResponse(totals.ToDto(), coupon?.Coupon?.ToDto(), coupon?.Reason, pricing.RemainingForFreeShipping(totals.Subtotal));
    }

    public async Task<CheckoutResponse> CreateAsync(CheckoutRequest request, Guid? userId, CancellationToken ct)
    {
        if (!Rules.Provinces.Contains(request.Province)) throw AppException.Unprocessable("Elegí una provincia", "invalid_province");

        var email = request.Email.Trim().ToLowerInvariant();

        // Idempotencia: el mismo intento (doble click, reintento de red) devuelve el mismo pedido.
        if (!string.IsNullOrWhiteSpace(request.ClientOrderId))
        {
            var existing = await db.Orders.Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Email == email && o.ClientOrderId == request.ClientOrderId, ct);
            if (existing is not null && existing.Status != OrderStatus.Cancelled) return await ResumeAsync(existing, ct);
        }

        var lines = await ResolveLinesAsync(request.Items, checkStock: true, ct);
        var pricingLines = lines.Select(l => new PricingLine(l.Product.Price, l.Quantity)).ToList();
        var subtotal = pricingLines.Sum(l => l.UnitPrice * l.Quantity);

        Coupon? coupon = null;
        if (!string.IsNullOrWhiteSpace(request.CouponCode))
        {
            var result = await coupons.ValidateAsync(request.CouponCode, subtotal, userId, ct);
            // Un cupon invalido se rechaza (no se ignora en silencio): el cliente no puede enterarse recien al pagar.
            if (!result.Valid) throw AppException.Unprocessable(result.Reason!, "coupon_invalid");
            coupon = result.Coupon;
        }

        var totals = pricing.Calculate(pricingLines, coupon, request.ShippingMethod, request.PaymentMethod);

        var order = new Order
        {
            PublicId = CodeGenerator.PublicOrderId(),
            Number = await NewOrderNumberAsync(ct),
            ClientOrderId = string.IsNullOrWhiteSpace(request.ClientOrderId) ? null : request.ClientOrderId.Trim(),
            UserId = userId,
            Email = email,
            Status = OrderStatus.Pending,
            ShippingMethod = request.ShippingMethod,
            PaymentMethod = request.PaymentMethod,
            CouponCode = coupon?.Code,
            Subtotal = totals.Subtotal,
            CouponDiscount = totals.CouponDiscount,
            PaymentDiscount = totals.PaymentDiscount,
            Shipping = totals.Shipping,
            Total = totals.Total,
            ExpiresAt = DateTime.UtcNow.AddHours(_commerce.PendingExpirationHours),
            Address = new ShippingAddress
            {
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                Email = email,
                Phone = request.Phone.Trim(),
                Street = request.Street.Trim(),
                Number = request.Number.Trim(),
                Apartment = Clean(request.Apartment),
                City = request.City.Trim(),
                Province = request.Province,
                Zip = request.Zip.Trim(),
                Notes = Clean(request.Notes),
            },
            Items = [.. lines.Select(l => new OrderItem
            {
                LineId = $"{l.Product.Id}:{l.Color.Id}:{l.Size}",
                ProductId = l.Product.Id,
                Slug = l.Product.Slug,
                Name = l.Product.Name,
                UnitPrice = l.Product.Price,
                ColorId = l.Color.Id,
                ColorName = l.Color.Name,
                ColorHex = l.Color.Hex,
                Size = l.Size,
                Quantity = l.Quantity,
                Kind = l.Product.Kind,
                Graphic = l.Product.Graphic,
            })],
        };

        // Reserva de stock + consumo del cupon + pedido: todo o nada.
        await lifecycle.InTransactionAsync(async () =>
        {
            await lifecycle.ReserveStockAsync(lines.Select(l => new OrderLifecycle.StockLine(l.Product.Id, l.Size, l.Quantity, l.Product.Name)), ct);

            if (coupon?.OwnerId is not null)
            {
                // Atomico: si dos pedidos intentan usar el mismo cupon personal, solo uno lo consigue.
                var couponId = coupon.Id;
                var orderId = order.Id;
                var used = await db.Coupons.Where(c => c.Id == couponId && c.UsedAt == null)
                    .ExecuteUpdateAsync(c => c.SetProperty(x => x.UsedAt, DateTime.UtcNow).SetProperty(x => x.UsedByOrderId, orderId), ct);
                if (used == 0) throw AppException.Unprocessable("Ya usaste este cupón.", "coupon_invalid");
            }

            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
        }, ct);

        if (userId is { } uid)
            await ActivityLog.LogAsync(db, uid, ActivityType.OrderPlaced, $"Realizaste el pedido {order.Number}", ct);

        var outcome = await StartPaymentAsync(order, ct);
        return new CheckoutResponse(order.ToDto(), outcome);
    }

    private async Task<PaymentOutcomeDto> StartPaymentAsync(Order order, CancellationToken ct)
    {
        if (order.PaymentMethod == PaymentMethodId.Transfer)
        {
            // Transferencia: queda pendiente hasta que el equipo confirme el deposito (POST /api/admin/orders/{id}/confirm-payment).
            order.PaymentProvider = "transfer";
            await db.SaveChangesAsync(ct);
            return new PaymentOutcomeDto("pending", null, _commerce.TransferInstructions, null);
        }

        GatewayResult result;
        try
        {
            result = await gateway.CreateCheckoutAsync(order, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo iniciar el pago del pedido {Number}", order.Number);
            // Compensacion: se libera el stock y el cupon para que el cliente pueda reintentar.
            await lifecycle.CancelAsync(order, "No se pudo iniciar el pago.", ct);
            if (ex is AppException) throw;
            throw new AppException(502, "gateway_error", "No pudimos procesar el pago. Intentá nuevamente.");
        }

        order.PaymentProvider = gateway.Id;
        order.TransactionId = result.TransactionId;
        order.PaymentRedirectUrl = result.RedirectUrl;

        if (result.Status == "approved")
        {
            OrderLifecycle.MarkPaid(order);
            db.Payments.Add(new Payment
            {
                OrderId = order.Id,
                Provider = gateway.Id,
                ExternalId = result.TransactionId ?? order.PublicId,
                Status = "approved",
                Amount = order.Total,
                Currency = _commerce.Currency,
            });
        }

        await db.SaveChangesAsync(ct);
        return new PaymentOutcomeDto(result.Status, result.RedirectUrl, result.Message, result.TransactionId);
    }

    /// <summary>Reintento con el mismo ClientOrderId: devuelve el estado actual en lugar de crear otro pedido.</summary>
    private async Task<CheckoutResponse> ResumeAsync(Order order, CancellationToken ct)
    {
        if (order.Status == OrderStatus.Pending && order.PaymentMethod != PaymentMethodId.Transfer && order.PaymentRedirectUrl is null && order.PaymentProvider == "")
        {
            // El pedido se creo pero el cliente nunca recibio el link de pago: se pide de nuevo.
            var outcome = await StartPaymentAsync(order, ct);
            return new CheckoutResponse(order.ToDto(), outcome);
        }

        var status = order.Status == OrderStatus.Pending ? "pending" : "approved";
        var message = order.PaymentMethod == PaymentMethodId.Transfer && order.Status == OrderStatus.Pending ? _commerce.TransferInstructions : null;
        return new CheckoutResponse(order.ToDto(), new PaymentOutcomeDto(status, order.Status == OrderStatus.Pending ? order.PaymentRedirectUrl : null, message, order.TransactionId));
    }

    private async Task<List<Line>> ResolveLinesAsync(List<CheckoutItemRequest> items, bool checkStock, CancellationToken ct)
    {
        // Mismo producto + color + talle repetido se junta en una sola linea (como el carrito del front).
        var merged = items
            .GroupBy(i => (i.ProductId, i.ColorId, i.Size))
            .Select(g => (g.Key.ProductId, g.Key.ColorId, g.Key.Size, Quantity: g.Sum(x => x.Quantity)))
            .ToList();

        var ids = merged.Select(m => m.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking().AsSplitQuery()
            .Include(p => p.ColorLinks).ThenInclude(l => l.Color)
            .Include(p => p.Stock)
            .Where(p => ids.Contains(p.Id) && p.Active)
            .ToListAsync(ct);

        var lines = new List<Line>();
        foreach (var m in merged)
        {
            var product = products.FirstOrDefault(p => p.Id == m.ProductId)
                          ?? throw AppException.Unprocessable("Un producto de tu carrito ya no está disponible.", "product_unavailable");
            var color = product.ColorLinks.FirstOrDefault(l => l.ColorId == m.ColorId)?.Color
                        ?? throw AppException.Unprocessable($"El color elegido de {product.Name} no está disponible.", "color_unavailable");
            if (!product.Sizes.Contains(m.Size))
                throw AppException.Unprocessable($"El talle {m.Size} no existe para {product.Name}.", "size_unavailable");
            if (m.Quantity > _commerce.MaxQuantityPerItem)
                throw AppException.Unprocessable($"Podés llevar hasta {_commerce.MaxQuantityPerItem} unidades de {product.Name}.", "quantity_exceeded");

            if (checkStock && (product.Stock.FirstOrDefault(s => s.Size == m.Size)?.Quantity ?? 0) < m.Quantity)
                throw AppException.Conflict($"Sin stock suficiente de {product.Name} ({m.Size}).", "out_of_stock");

            lines.Add(new Line(product, color, m.Size, m.Quantity));
        }

        return lines;
    }

    private async Task<string> NewOrderNumberAsync(CancellationToken ct)
    {
        string number;
        do
        {
            number = CodeGenerator.OrderNumber();
        } while (await db.Orders.AnyAsync(o => o.Number == number, ct));

        return number;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
