using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Npgsql;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-29 contra el esquema migrado: <c>messaging.history_syncs</c> guarda los estados por nombre y la versión,
/// sus CHECKs los hace cumplir la base, y la migración trae la cola de idempotencia y <c>history_managed</c>.</summary>
public sealed class HistorySyncPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASyncRoundTripsWithItsStatesAndVersion()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var connectedAt = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            dbContext.HistorySyncs.Add(HistorySync.Start(tenantId, connectionId, connectedAt, connectedAt));
            await dbContext.SaveChangesAsync(Ct);
        }

        Assert.Equal("Pending|Pending|0|1", await ScalarAsync<string>(connectionString,
            "SELECT contacts_state || '|' || history_state || '|' || attempts || '|' || version FROM messaging.history_syncs WHERE connection_id = @id",
            ("id", connectionId)));
        await ExecuteAsync(connectionString,
            "UPDATE messaging.history_syncs SET contacts_state = 'Requested', contacts_request_id = 'req-1', history_state = 'Failed', history_error_code = 'graph_rejected' WHERE connection_id = @id",
            ("id", connectionId));

        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var sync = await dbContext.HistorySyncs.SingleAsync(value => value.ConnectionId == connectionId, Ct);
            Assert.Equal(tenantId, sync.TenantId);
            Assert.Equal(connectedAt, sync.ConnectedAt);
            Assert.Equal(HistorySyncRequestState.Requested, sync.ContactsState);
            Assert.Equal("req-1", sync.ContactsRequestId);
            Assert.Equal(HistorySyncRequestState.Failed, sync.HistoryState);
            Assert.Equal("graph_rejected", sync.HistoryErrorCode);
            Assert.Equal(1, sync.Version);

            sync.Retry(connectedAt.AddHours(1));
            await dbContext.SaveChangesAsync(Ct);
        }

        Assert.Equal("Requested|Pending||2", await ScalarAsync<string>(connectionString,
            "SELECT contacts_state || '|' || history_state || '|' || coalesce(history_error_code, '') || '|' || version FROM messaging.history_syncs WHERE connection_id = @id",
            ("id", connectionId)));
    }

    [Fact]
    public async Task TheProgressCheckRejectsAValueAbove100()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var connectionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            dbContext.HistorySyncs.Add(HistorySync.Start(Guid.CreateVersion7(), connectionId, now, now));
            await dbContext.SaveChangesAsync(Ct);
        }

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
            "UPDATE messaging.history_syncs SET history_progress = 101 WHERE connection_id = @id", ("id", connectionId)));

        Assert.Equal("23514", error.SqlState);
        Assert.Equal("CK_history_syncs_progress", error.ConstraintName);
    }

    [Fact]
    public async Task TheMigrationAddsTheInboxTheIndexesAndHistoryManaged()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        _ = factory.Services;

        Assert.Contains("WHERE (((contacts_state)::text = 'Pending'::text) OR ((history_state)::text = 'Pending'::text))",
            await ScalarAsync<string>(connectionString,
                "SELECT indexdef FROM pg_indexes WHERE schemaname = 'messaging' AND indexname = 'IX_history_syncs_pending'"),
            StringComparison.Ordinal);
        Assert.Equal(1L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'messaging' AND indexname = 'IX_history_syncs_tenant'"));
        Assert.Equal("consumer,message_id", await ScalarAsync<string>(connectionString,
            "SELECT string_agg(a.attname, ',' ORDER BY array_position(i.indkey, a.attnum)) FROM pg_index i JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey) WHERE i.indrelid = 'messaging.inbox_messages'::regclass AND i.indisprimary"));
        Assert.Equal("boolean|NO|false", await ScalarAsync<string>(connectionString,
            "SELECT data_type || '|' || is_nullable || '|' || column_default FROM information_schema.columns WHERE table_schema = 'messaging' AND table_name = 'conversations' AND column_name = 'history_managed'"));
    }
}
