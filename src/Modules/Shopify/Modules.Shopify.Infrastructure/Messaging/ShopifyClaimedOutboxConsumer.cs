using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Infrastructure.Persistence;

namespace Modules.Shopify.Infrastructure.Messaging;

/// <summary>
/// Copia de <c>ClaimedOutboxConsumer</c> de Pipeline (ningún módulo referencia la infraestructura de otro): armar el lote, reclamar
/// cada mensaje, procesarlo en su propio scope y no dejar que una falla corte el lote.
/// </summary>
/// <remarks>
/// <para><b>Lote.</b> Los mensajes de sus eventos sin fila en el inbox, o reclamados y sin terminar con el reclamo vencido, en
/// orden de llegada. Uno que espera su reintento no entra, así que no le quita lugar a otro.</para>
/// <para><b>Reclamo.</b> <see cref="ShopifyInboxClaims"/> antes de procesar: una sentencia que se commitea sola y suma el
/// intento. El reclamo número n dura <c>leases[n - 1]</c>, y desde el último en adelante, el último. Nunca se abandona un mensaje:
/// un evento perdido dejaría pedidos esperando una ciudad que ya tiene sinónimo.</para>
/// <para><b>Un scope por mensaje.</b> Con su propio <see cref="ShopifyDbContext"/>: lo que dejó rastreado un mensaje que falla
/// muere con su scope.</para>
/// </remarks>
internal sealed class ShopifyClaimedOutboxConsumer(
    IServiceScopeFactory scopeFactory,
    string consumer,
    IReadOnlyList<string> eventNames,
    IReadOnlyList<TimeSpan> leases)
{
    public const int BatchSize = 20;

    public IReadOnlyList<TimeSpan> Leases => leases;

    public async Task ProcessBatchAsync(
        Func<ShopifyClaimedMessage, CancellationToken, Task> process,
        Action<Exception, ShopifyOutboxRecord, int?> onFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(onFailure);
        List<ShopifyOutboxRecord> pending;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<ShopifyDbContext>();
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
                var dbContext = services.GetRequiredService<ShopifyDbContext>();
                var now = services.GetRequiredService<IClock>().UtcNow;
                attempts = await ShopifyInboxClaims.TryClaimAsync(dbContext, consumer, record.Id, now, leases, cancellationToken);
                if (attempts is null)
                {
                    // Otra réplica lo tiene, o ya se terminó desde que se armó el lote.
                    continue;
                }

                await process(new ShopifyClaimedMessage(services, dbContext, record, now), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // El intento ya quedó contado en el reclamo: el mensaje vuelve cuando ese reclamo venza, siempre.
                onFailure(exception, record, attempts);
            }
        }
    }

    /// <summary>Marca como terminada la fila que dejó el reclamo, sin guardar: quien llama la guarda en el mismo SaveChanges que su
    /// efecto, así que un mensaje procesado nunca queda sin marcar.</summary>
    public async Task MarkProcessedAsync(ShopifyClaimedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var entry = await message.DbContext.Inbox.SingleAsync(
            candidate => candidate.Consumer == consumer && candidate.MessageId == message.Record.Id, cancellationToken);
        entry.ProcessedAt = message.Now;
    }
}

/// <summary>Un mensaje ya reclamado, con el scope en el que se procesa y el instante del reclamo.</summary>
internal sealed record ShopifyClaimedMessage(IServiceProvider Services, ShopifyDbContext DbContext, ShopifyOutboxRecord Record, DateTimeOffset Now);
