using Microsoft.EntityFrameworkCore;
using BuildingBlocks.Application;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Messaging;

internal interface IOutboxProcessor
{
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

// Publicador interno del Outbox para el monolito modular (sin broker externo). Reclama
// un lote de mensajes sin procesar con FOR UPDATE SKIP LOCKED para que varios workers
// nunca tomen la misma fila, despacha cada uno y marca processed_at. Un despacho
// fallido registra attempts/last_error y se reintenta en un tick posterior.
internal sealed class OutboxProcessor(
    TenancyDbContext dbContext,
    IIntegrationEventDispatcher dispatcher,
    IClock clock) : IOutboxProcessor
{
    private const int BatchSize = 20;

    private static readonly string ClaimSql =
        "SELECT * FROM platform.outbox_messages " +
        "WHERE processed_at IS NULL " +
        "ORDER BY occurred_at " +
        "FOR UPDATE SKIP LOCKED " +
        "LIMIT " + BatchSize;

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var messages = await dbContext.OutboxMessages
            .FromSqlRaw(ClaimSql)
            .ToListAsync(cancellationToken);

        if (messages.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return 0;
        }

        var processed = 0;
        foreach (var message in messages)
        {
            try
            {
                await dispatcher.DispatchAsync(message, cancellationToken);
                message.ProcessedAt = clock.UtcNow;
                processed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = exception.Message;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return processed;
    }
}
