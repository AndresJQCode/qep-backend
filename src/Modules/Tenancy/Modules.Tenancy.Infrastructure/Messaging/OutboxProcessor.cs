using Microsoft.EntityFrameworkCore;
using BuildingBlocks.Application;
using Modules.Tenancy.Infrastructure.Persistence;

namespace Modules.Tenancy.Infrastructure.Messaging;

internal interface IOutboxProcessor
{
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}

// Internal Outbox publisher for the modular monolith (no external broker). It
// claims a batch of unprocessed messages with FOR UPDATE SKIP LOCKED so multiple
// workers never grab the same row, dispatches each, and marks processed_at. A
// failed dispatch records attempts/last_error and is retried on a later tick.
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
