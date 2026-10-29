using BirvennApi.Options;
using Microsoft.Extensions.Options;

namespace BirvennApi.Services;

/// <summary>
/// Cada tanto cancela los pedidos que siguen sin pagar pasado su plazo (48 h por defecto) y devuelve el stock
/// y el cupon. Es seguro correrlo en varias instancias: la cancelacion es atomica.
/// </summary>
public class PendingOrderExpirationWorker(
    IServiceScopeFactory scopes,
    IOptions<CommerceOptions> options,
    ILogger<PendingOrderExpirationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.ExpirationIntervalSeconds)));

        try
        {
            do
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var cancelled = await scope.ServiceProvider.GetRequiredService<OrderLifecycle>().ExpirePendingAsync(stoppingToken);
                    if (cancelled > 0) logger.LogInformation("{Count} pedido(s) vencido(s) cancelado(s)", cancelled);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Un fallo puntual (base caida un momento) no tiene que matar el worker.
                    logger.LogError(ex, "Falló la pasada de vencimiento de pedidos");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // apagado normal
        }
    }
}
