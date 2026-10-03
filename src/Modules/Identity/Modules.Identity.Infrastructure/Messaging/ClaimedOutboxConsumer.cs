using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Identity.Infrastructure.Persistence;

namespace Modules.Identity.Infrastructure.Messaging;

/// <summary>
/// El esqueleto que comparten los consumidores del Outbox de plataforma en Identity
/// (<see cref="OrphanUserCleanupWorker"/> y <see cref="SessionRevocationWorker"/>): armar el lote,
/// reclamar cada mensaje, procesarlo en su propio scope y no dejar que una falla corte el lote.
/// Lo que cada worker hace con el mensaje, cuánto espera entre intentos y cómo loguea una falla
/// lo decide él; acá no hay nada de eso.
/// </summary>
/// <remarks>
/// <para><b>Lote.</b> Los mensajes de sus eventos sin fila en el inbox, o reclamados y sin
/// terminar con el reclamo vencido, en orden de llegada. Uno que espera su reintento no entra, así
/// que no le quita lugar a otro.</para>
/// <para><b>Reclamo.</b> <see cref="IdentityInboxClaims"/> antes de procesar: una sentencia que se
/// commitea sola y suma el intento, así que el intento queda contado aunque la unidad de trabajo
/// del mensaje falle y se descarte entera. El reclamo número n dura <c>leases[n - 1]</c>, y desde
/// el último en adelante, el último: es a la vez el lease de quien procesa y la espera antes del
/// reintento. Nunca se abandona un mensaje (spec 2026-10-02).</para>
/// <para><b>Un scope por mensaje.</b> Con sus propios DbContexts: lo que un mensaje que falla dejó
/// rastreado —en Identity o en otro módulo— muere con su scope y no lo commitea el siguiente.</para>
/// </remarks>
internal sealed class ClaimedOutboxConsumer(
    IServiceScopeFactory scopeFactory,
    string consumer,
    IReadOnlyList<string> eventNames,
    IReadOnlyList<TimeSpan> leases)
{
    public const int BatchSize = 20;

    /// <summary>La curva de este consumidor: el único lugar donde vive una vez armado el worker.
    /// La espera después del intento fallido número n es <c>Leases[n - 1]</c>, y desde el último,
    /// el último.</summary>
    public IReadOnlyList<TimeSpan> Leases => leases;

    /// <summary>La regla de <see cref="IdentityInboxClaims"/> en C#: cuánto dura el reclamo
    /// número <paramref name="attempt"/> con <see cref="Leases"/>. Las pruebas avanzan el reloj con
    /// esto.</summary>
    public TimeSpan LeaseFor(int attempt) => leases[Math.Min(attempt, leases.Count) - 1];

    /// <summary>
    /// Procesa un lote. <paramref name="process"/> recibe el mensaje ya reclamado y tiene que
    /// llamar a <see cref="MarkProcessedAsync"/> antes de su <c>SaveChanges</c>, para que el inbox
    /// quede terminado junto con el efecto. <paramref name="onFailure"/> recibe los intentos
    /// contando el que falló, o <c>null</c> si lo que falló fue el reclamo mismo y no se contó
    /// nada.
    /// </summary>
    public async Task ProcessBatchAsync(
        Func<ClaimedMessage, CancellationToken, Task> process,
        Action<Exception, OutboxRecord, int?> onFailure,
        CancellationToken cancellationToken)
    {
        List<OutboxRecord> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<IClock>().UtcNow;
            pending = await dbContext.Outbox
                .AsNoTracking()
                .Where(record => eventNames.Contains(record.EventName))
                .Where(record => !dbContext.Inbox.Any(entry =>
                    entry.Consumer == consumer
                    && entry.MessageId == record.Id
                    && (entry.ProcessedAt != null || entry.ClaimedUntil >= now)))
                .OrderBy(record => record.OccurredAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
        }

        foreach (var record in pending)
        {
            await using var recordScope = scopeFactory.CreateAsyncScope();
            var services = recordScope.ServiceProvider;
            int? attempts = null;
            try
            {
                var dbContext = services.GetRequiredService<IdentityDbContext>();
                var now = services.GetRequiredService<IClock>().UtcNow;
                attempts = await IdentityInboxClaims.TryClaimAsync(
                    dbContext, consumer, record.Id, now, leases, cancellationToken);
                if (attempts is null)
                {
                    // Otra réplica lo tiene, o ya se terminó desde que se armó el lote.
                    continue;
                }

                await process(new ClaimedMessage(services, dbContext, record, now), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Un mensaje que falla no frena a los demás del lote ni al loop, y lo que dejó
                // rastreado muere con su scope. El intento ya quedó contado en el reclamo: el
                // mensaje vuelve cuando ese reclamo venza, siempre.
                onFailure(exception, record, attempts);
            }
        }
    }

    /// <summary>
    /// Marca como terminada la fila que dejó el reclamo, sin guardar: quien llama la guarda en el
    /// mismo <c>SaveChanges</c> —y la misma transacción— que su efecto, así que un mensaje
    /// procesado nunca queda sin marcar.
    /// </summary>
    public async Task MarkProcessedAsync(ClaimedMessage message, CancellationToken cancellationToken)
    {
        var entry = await message.DbContext.Inbox.SingleAsync(
            candidate => candidate.Consumer == consumer && candidate.MessageId == message.Record.Id,
            cancellationToken);
        entry.ProcessedAt = message.Now;
    }
}

/// <summary>Un mensaje ya reclamado, con el scope en el que se procesa y el instante del
/// reclamo.</summary>
internal sealed record ClaimedMessage(
    IServiceProvider Services,
    IdentityDbContext DbContext,
    OutboxRecord Record,
    DateTimeOffset Now);
