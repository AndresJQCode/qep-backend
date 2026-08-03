using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

// Acceptance #6: reprocessing the integration event must not duplicate effects.
// The Outbox worker publishes the tenant-settings event to the change-log
// projection (an append-only, non-idempotent effect). Redelivering the same
// message must still leave a single projection row thanks to the Inbox guard.
public sealed class OutboxIdempotencyTests
{
    private const string EventName = "tenancy.tenant-settings-updated.v1";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] ChangedFields = ["displayName"];

    [Fact]
    public async Task ReprocessingTheIntegrationEventDoesNotDuplicateEffects()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);

        // Creating a client starts the host, which runs the Outbox worker.
        using var client = factory.CreateClient();

        var eventId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await SeedOutboxMessageAsync(connection, eventId, tenantId);

        // First delivery: the worker applies the effect exactly once.
        await WaitUntilAsync(
            async () => await CountChangeLogAsync(connection, eventId) == 1);
        Assert.Equal(1, await CountInboxAsync(connection, eventId));

        // Redelivery: reset processed_at so the worker claims the row again.
        await ResetProcessedAtAsync(connection, eventId);
        await WaitUntilAsync(
            async () => await IsProcessedAsync(connection, eventId));

        // The Inbox guard suppressed the duplicate effect.
        Assert.Equal(1, await CountChangeLogAsync(connection, eventId));
        Assert.Equal(1, await CountInboxAsync(connection, eventId));
    }

    private static async Task SeedOutboxMessageAsync(
        NpgsqlConnection connection,
        Guid eventId,
        Guid tenantId)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            eventId,
            occurredAt,
            tenantId = new { value = tenantId },
            version = 2L,
            changedFields = ChangedFields
        });

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.outbox_messages
                (id, event_name, payload, correlation_id, occurred_at, attempts)
            VALUES (@id, @eventName, @payload::jsonb, @correlationId, @occurredAt, 0)
            """,
            connection);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("eventName", EventName);
        command.Parameters.AddWithValue("payload", payload);
        command.Parameters.AddWithValue("correlationId", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("occurredAt", occurredAt);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ResetProcessedAtAsync(NpgsqlConnection connection, Guid eventId)
    {
        await using var command = new NpgsqlCommand(
            "UPDATE platform.outbox_messages SET processed_at = NULL WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", eventId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<bool> IsProcessedAsync(NpgsqlConnection connection, Guid eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT processed_at IS NOT NULL FROM platform.outbox_messages WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", eventId);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is true;
    }

    private static async Task<long> CountChangeLogAsync(NpgsqlConnection connection, Guid eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM tenancy.tenant_settings_change_log WHERE event_id = @id",
            connection);
        command.Parameters.AddWithValue("id", eventId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<long> CountInboxAsync(NpgsqlConnection connection, Guid eventId)
    {
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM platform.inbox_messages WHERE message_id = @id",
            connection);
        command.Parameters.AddWithValue("id", eventId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Condition was not met within {PollTimeout.TotalSeconds:0} seconds.");
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
        }
    }
}
