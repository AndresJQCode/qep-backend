using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
using Modules.Siigo.Domain;
using Modules.Siigo.Infrastructure.Persistence;

namespace Modules.Siigo.Infrastructure.Batches;

/// <summary>
/// Las tandas de facturación con Siigo, de a un pedido. Copia el patrón de <c>CampaignDispatchWorker</c> (WhatsApp): timbre
/// (<see cref="DoorbellQueues.SiigoInvoice"/>) con red de seguridad, reclamo con <c>FOR UPDATE SKIP LOCKED</c> y lease, y recuperación del lease
/// vencido por el mismo reclamo; varias réplicas pueden correrlo a la vez y cada pedido tiene un solo ganador. A diferencia de las
/// campañas, <b>un pedido por vez por réplica</b>: cada factura son varias llamadas a Siigo y su cuenta bloquea al 80 % de fallidas.
/// Ninguna transacción queda abierta mientras se llama a Siigo: el reclamo es una sentencia sola, la factura la crea el handler de siempre
/// (con su candado por pedido y su clave de idempotencia, así que repetir un pedido no duplica) y el resultado se guarda aparte.
/// Un pedido cuyo lease vence con el proceso muerto vuelve a reclamarse; al pasar de <see cref="SiigoInvoiceBatchItem.MaxAttempts"/> reclamos
/// se da por fallido. Lo que pasa con un pedido es un resultado (<see cref="SiigoBatchItemOutcome"/>), nunca una excepción que frene la tanda.
/// </summary>
internal sealed partial class SiigoInvoiceBatchWorker(
    IQueueDoorbell doorbell,
    IServiceScopeFactory scopeFactory,
    IOptions<SiigoInvoiceBatchOptions> options,
    TimeProvider time,
    ILogger<SiigoInvoiceBatchWorker> logger) : BackgroundService
{
    /// <summary>Holgado frente a una factura entera: buscar o crear el cliente, la factura y la de flete, a 30 s de timeout cada llamada.</summary>
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    [LoggerMessage(Level = LogLevel.Error, Message = "Siigo invoice batch tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Invoicing batch item {ItemId} threw; it is retried when its lease expires.")]
    private static partial void LogItemThrew(ILogger logger, Exception exception, Guid itemId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = doorbell.Listen(DoorbellQueues.SiigoInvoice);
        await DoorbellPolling.RunAsync(
            listener,
            TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds)),
            DoorbellQueues.SafetyNet(DoorbellQueues.SiigoInvoice),
            async _ =>
            {
                try
                {
                    return await DrainAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogTickFailed(logger, exception);
                    return (TimeSpan?)null;
                }
            },
            stoppingToken);
    }

    /// <summary>Una pasada (internal para las pruebas): procesa pedidos mientras haya uno listo, hasta gastar un intervalo. Devuelve el
    /// próximo vencimiento conocido: <c>null</c> si no queda nada pendiente, cero si se cortó por tiempo, o lo que falta para el pedido
    /// que espera su reintento.</summary>
    internal async Task<TimeSpan?> DrainAsync(CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var span = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollSeconds));
        while (await ProcessNextAsync(cancellationToken))
        {
            if (time.GetElapsedTime(started) >= span)
            {
                return TimeSpan.Zero;
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SiigoDbContext>();
        var next = await dbContext.InvoiceBatchItems
            .AsNoTracking()
            .Where(item => item.Status == SiigoInvoiceBatchItemStatus.Pending)
            .MinAsync(item => (DateTimeOffset?)item.AvailableAt, cancellationToken);
        return next is { } due ? due - time.GetUtcNow() is { } wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero : null;
    }

    /// <summary>Reclama y procesa un pedido. <c>false</c> si no había ninguno listo.</summary>
    private async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(cancellationToken);
        if (claim is null)
        {
            return false;
        }

        try
        {
            await ProcessAsync(claim, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // El lease vence y el pedido vuelve a reclamarse: la factura que quedara a medias la reanuda el handler con la misma clave.
            LogItemThrew(logger, exception, claim.ItemId);
        }

        return true;
    }

    private async Task<Claim?> ClaimAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var leaseUntil = now + Lease;
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SiigoDbContext>();
        // Una sola sentencia con el estado como guarda: dos réplicas no reclaman el mismo pedido, y SKIP LOCKED evita esperarse. El orden es
        // el de las tandas y, dentro de cada una, el de los pedidos.
        var claimed = await dbContext.Database.SqlQuery<ClaimRow>(
            $"""
            UPDATE siigo.invoice_batch_items
               SET status = 'Sending', attempts = attempts + 1, lease_until = {leaseUntil}, updated_at = {now}
             WHERE id = (SELECT i.id
                           FROM siigo.invoice_batch_items i
                          WHERE (i.status = 'Pending' AND i.available_at <= {now})
                             OR (i.status = 'Sending' AND i.lease_until < {now})
                          ORDER BY (SELECT b.created_at FROM siigo.invoice_batches b WHERE b.id = i.batch_id), i.position
                          LIMIT 1
                            FOR UPDATE SKIP LOCKED)
            RETURNING id AS "ItemId", tenant_id AS "TenantId", batch_id AS "BatchId", order_id AS "OrderId", attempts AS "Attempts"
            """).ToListAsync(cancellationToken);
        return claimed.Count == 0
            ? null
            : new Claim(claimed[0].ItemId, claimed[0].TenantId, claimed[0].BatchId, claimed[0].OrderId, claimed[0].Attempts);
    }

    private async Task ProcessAsync(Claim claim, CancellationToken cancellationToken)
    {
        Guid requestedBy;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var batch = await scope.ServiceProvider.GetRequiredService<ISiigoInvoiceBatchRepository>()
                .FindAsync(claim.TenantId, claim.BatchId, cancellationToken);
            if (batch is null)
            {
                return;
            }

            if (claim.Attempts > SiigoInvoiceBatchItem.MaxAttempts)
            {
                // Reclamado más veces de las que se permiten (el proceso murió en cada una): no se vuelve a intentar.
                await SettleAsync(scope, batch, claim.ItemId, new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.Unavailable), cancellationToken);
                return;
            }

            requestedBy = batch.RequestedBy;
        }

        SiigoBatchItemOutcome outcome;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            outcome = await scope.ServiceProvider.GetRequiredService<ISiigoBatchItemInvoker>()
                .InvokeAsync(claim.TenantId, requestedBy, claim.OrderId, cancellationToken);
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var batch = await scope.ServiceProvider.GetRequiredService<ISiigoInvoiceBatchRepository>()
                .FindAsync(claim.TenantId, claim.BatchId, cancellationToken);
            if (batch is not null)
            {
                await SettleAsync(scope, batch, claim.ItemId, outcome, cancellationToken);
            }
        }
    }

    private async Task SettleAsync(
        AsyncServiceScope scope, SiigoInvoiceBatch batch, Guid itemId, SiigoBatchItemOutcome outcome, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var item = batch.Items.Single(value => value.Id == itemId);
        switch (outcome)
        {
            case SiigoBatchItemOutcome.Invoiced invoiced:
                item.MarkInvoiced(invoiced.Number, now);
                break;
            case SiigoBatchItemOutcome.Skipped skipped:
                item.MarkSkipped(skipped.ErrorCode, skipped.Missing, now);
                break;
            case SiigoBatchItemOutcome.Failed failed:
                item.MarkFailed(failed.ErrorCode, now);
                break;
            case SiigoBatchItemOutcome.Retry retry:
                item.Retry(retry.ErrorCode, now + TimeSpan.FromMinutes(Math.Min(item.Attempts, SiigoInvoiceBatchItem.MaxAttempts)), now);
                break;
            default:
                throw new InvalidOperationException("Unknown batch item outcome.");
        }

        batch.CloseIfFinished(now);
        await scope.ServiceProvider.GetRequiredService<ISiigoUnitOfWork>().SaveChangesAsync(cancellationToken);
    }

    private sealed record Claim(Guid ItemId, Guid TenantId, Guid BatchId, Guid OrderId, int Attempts);

    private sealed record ClaimRow(Guid ItemId, Guid TenantId, Guid BatchId, Guid OrderId, int Attempts);
}
