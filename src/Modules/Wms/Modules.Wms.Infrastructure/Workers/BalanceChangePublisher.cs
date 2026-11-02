using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Wms.Application;

namespace Modules.Wms.Infrastructure.Workers;

/// <summary>
/// Convierte la cola de claves tocadas en <c>wms.balance.changed.v1</c>: un evento por <c>(dueño, bodega, variante)</c> por vuelta,
/// con el saldo final, sin importar cuántos movimientos lo tocaron (RNF-14).
/// <para>
/// El orden dentro de la transacción es la garantía: primero se saca la clave de la cola y después se lee el saldo. Un escritor que
/// ya commiteó antes del <c>DELETE</c> está en el saldo que se lee; uno que commitea después vuelve a encolar la clave (la fila
/// borrada se libera al commit de esta vuelta) y la publica la siguiente. Al revés —leer y luego sacar— el cambio posterior a la
/// lectura se perdería. La transacción hace sólo eso: el <c>DELETE</c> deja las filas de la cola bloqueadas y los escritores de
/// esas claves esperan el commit con sus saldos tomados, así que no hay más trabajo que la lectura de las 500 claves y el outbox.
/// </para>
/// </summary>
internal sealed partial class BalanceChangePublisher(
    IServiceScopeFactory scopeFactory,
    IOptions<WmsWorkerOptions> options,
    ILogger<BalanceChangePublisher> logger) : BackgroundService
{
    private const int BatchSize = 500;

    [LoggerMessage(Level = LogLevel.Error, Message = "Wms balance change publishing failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        WorkerLoop.RunAsync(
            TimeSpan.FromSeconds(options.Value.WorkerStartupSeconds),
            TimeSpan.FromSeconds(options.Value.BalanceChangePublishSeconds),
            async token =>
            {
                // Una vuelta llena probablemente deja más en la cola: se drena antes de esperar el siguiente periodo.
                while (await PublishOnceAsync(token) == BatchSize)
                {
                }
            },
            exception => LogTickFailed(logger, exception),
            stoppingToken);

    /// <summary>Una vuelta: devuelve cuántos eventos publicó. Si algo falla, la transacción se deshace y las claves vuelven a la cola
    /// porque el <c>DELETE … RETURNING</c> no commiteó.</summary>
    internal async Task<int> PublishOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IWmsUnitOfWork>();
        var queue = scope.ServiceProvider.GetRequiredService<IBalanceChangeQueue>();
        var reads = scope.ServiceProvider.GetRequiredService<IStockReadQueries>();
        var events = scope.ServiceProvider.GetRequiredService<IWmsEventPublisher>();

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var keys = await queue.DequeueAllAsync(BatchSize, cancellationToken);
        if (keys.Count == 0)
        {
            return 0;
        }

        // El reloj es scoped en el composition root: se resuelve en la vuelta, no en el constructor del singleton.
        var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
        foreach (var key in keys)
        {
            var summary = await reads.SummarizeAsync(key.TenantId, key.WarehouseId, key.OwnerId, key.VariantId, cancellationToken);
            events.PublishBalanceChanged(
                new BalanceChangedPayload(
                    key.TenantId, key.OwnerId, key.WarehouseId, key.VariantId, summary.OnHand, summary.Reserved, summary.Available, now),
                now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return keys.Count;
    }
}
