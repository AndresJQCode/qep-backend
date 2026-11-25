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
/// (con su candado por pedido y su clave de idempotencia) y el resultado se guarda aparte.
/// Cada pedido se manda una sola vez (<see cref="SiigoInvoiceBatchItem.MaxAttempts"/>): no se programan reintentos. Uno que quedó sin
/// resultado (el lease venció con el proceso muerto, o el apagado cortó el envío) toma el estado de sus filas en <c>siigo.invoices</c> sin
/// llamar a Siigo: por confirmar, facturado o, si no alcanzó a quedar una fila o la de flete de una factura que ya existe sigue
/// <c>Pending</c>, fallido. Lo que pasa con un pedido es un resultado (<see cref="SiigoBatchItemOutcome"/>), nunca una excepción que
/// frene la tanda.
/// </summary>
internal sealed partial class SiigoInvoiceBatchWorker(
    IQueueDoorbell doorbell,
    IServiceScopeFactory scopeFactory,
    IOptions<SiigoInvoiceBatchOptions> options,
    TimeProvider time,
    ILogger<SiigoInvoiceBatchWorker> logger) : BackgroundService
{
    /// <summary>Mayor que <see cref="SiigoInvoice.SendLease"/>: un pedido no se recupera mientras su factura todavía se considera en
    /// envío. Y mayor que el peor pedido, con cada factura reenviada por redondeo, porque este lease no se renueva.</summary>
    internal static readonly TimeSpan Lease = TimeSpan.FromMinutes(12);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Reading whether order {OrderId} carries its Siigo invoice failed; the batch item says the order could not be marked.")]
    private static partial void LogMarkReadFailed(ILogger logger, Exception exception, Guid orderId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Siigo invoice batch tick failed.")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Invoicing batch item {ItemId} threw; it takes the state of its invoice when its lease expires.")]
    private static partial void LogItemThrew(ILogger logger, Exception exception, Guid itemId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Batch item {ItemId} was cut short by the shutdown and could not be saved; it is recovered when its lease expires.")]
    private static partial void LogCutShortNotSaved(ILogger logger, Exception exception, Guid itemId);

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
    /// próximo vencimiento conocido: <c>null</c> si no queda nada pendiente, cero si se cortó por tiempo, o lo que falta para el próximo
    /// pedido disponible.</summary>
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
            // El lease vence, el pedido vuelve a reclamarse y toma el estado de su fila sin llamar a Siigo otra vez.
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
        IReadOnlyList<SiigoPaymentChoice>? payments;
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
                // Ya se reclamó una vez y no quedó resultado (el proceso murió a mitad del envío): no se vuelve a mandar.
                var recovered = await RecoverAsync(claim.TenantId, claim.OrderId, cancellationToken);
                await SettleAsync(scope, batch, claim.ItemId, recovered, cancellationToken);
                return;
            }

            requestedBy = batch.RequestedBy;
            payments = SiigoBatchPayments.Deserialize(batch.Items.Single(value => value.Id == claim.ItemId).PaymentsJson);
        }

        SiigoBatchItemOutcome outcome;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            outcome = await scope.ServiceProvider.GetRequiredService<ISiigoBatchItemInvoker>()
                .InvokeAsync(claim.TenantId, requestedBy, claim.OrderId, payments, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CloseCutShortAsync(claim);
            throw;
        }

        await SaveOutcomeAsync(claim, outcome, cancellationToken);
    }

    // El apagado cortó el envío: un último guardado corto, sin el token ya cancelado, para no dejar el pedido Sending hasta que venza
    // el lease. Si no alcanza, lo cierra esa recuperación; en ningún caso se tapa la cancelación.
    private async Task CloseCutShortAsync(Claim claim)
    {
        try
        {
            var recovered = await RecoverAsync(claim.TenantId, claim.OrderId, CancellationToken.None);
            await SaveOutcomeAsync(claim, recovered, CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogCutShortNotSaved(logger, exception, claim.ItemId);
        }
    }

    // El resultado de un pedido cuyo envío no dejó ninguno: lo dice la fila viva más nueva del pedido en siigo.invoices. Nunca llama a
    // Siigo (spec 2026-11-24 §4). Internal para las pruebas.
    internal async Task<SiigoBatchItemOutcome> RecoverAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ISiigoInvoiceRepository>()
            .ListByOrderAsync(tenantId, orderId, cancellationToken);
        // Vienen de la más vieja a la más nueva.
        var row = rows.LastOrDefault(value => value.Kind == SiigoInvoiceKind.Order && value.IsLive);
        // Con la del pedido en Siigo y la de flete todavía Pending, «facturado» la dejaría sin mandar a la vista de nadie (la misma regla
        // del ejecutor; decidido el 2026-11-25). No se lee la marca: las filas del pedido la avisan al leerse y el reintento la repite.
        if (row is { IsOpen: false }
            && rows.Any(value => value is { Kind: SiigoInvoiceKind.Freight, Status: SiigoInvoiceStatus.Pending }))
        {
            return new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceFreightPending);
        }

        return row switch
        {
            null => new SiigoBatchItemOutcome.Failed(SiigoErrorCodes.InvoiceInterrupted),
            { IsOpen: true } => new SiigoBatchItemOutcome.ToConfirm(row.Id, row.ErrorCode ?? SiigoErrorCodes.InvoiceInterrupted),
            _ => new SiigoBatchItemOutcome.Invoiced(
                row.Number ?? string.Empty,
                row.Id,
                OrderMarkFailed: !await IsOrderMarkedAsync(scope.ServiceProvider, tenantId, orderId, row.Number, cancellationToken)),
        };
    }

    // La factura existe, pero el envío pudo morir antes de marcar el pedido, o la marca haber fallado: se pregunta, sin escribir. Sin
    // confirmación el ítem avisa que el pedido no quedó marcado; reenviarlo lo marca sin crear otra factura. Que la lectura falle no
    // impide cerrar el pedido.
    private async Task<bool> IsOrderMarkedAsync(
        IServiceProvider services, Guid tenantId, Guid orderId, string? number, CancellationToken cancellationToken) =>
        number is not null
        && await SiigoOrderMark.IsConfirmedAsync(
            services.GetRequiredService<ISiigoOrderSource>(),
            tenantId,
            orderId,
            number,
            exception => LogMarkReadFailed(logger, exception, orderId),
            cancellationToken);

    private async Task SaveOutcomeAsync(Claim claim, SiigoBatchItemOutcome outcome, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var batch = await scope.ServiceProvider.GetRequiredService<ISiigoInvoiceBatchRepository>()
            .FindAsync(claim.TenantId, claim.BatchId, cancellationToken);
        if (batch is not null)
        {
            await SettleAsync(scope, batch, claim.ItemId, outcome, cancellationToken);
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
                item.MarkInvoiced(invoiced.Number, now, invoiced.InvoiceId, invoiced.OrderMarkFailed);
                break;
            case SiigoBatchItemOutcome.Skipped skipped:
                item.MarkSkipped(skipped.ErrorCode, skipped.Missing, now);
                break;
            case SiigoBatchItemOutcome.Failed failed:
                item.MarkFailed(failed.ErrorCode, now, failed.InvoiceId);
                break;
            case SiigoBatchItemOutcome.ToConfirm toConfirm:
                item.MarkToConfirm(toConfirm.InvoiceId, toConfirm.ErrorCode, now);
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
