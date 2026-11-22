using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Notifications.Infrastructure.InApp;
using Npgsql;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>Spec 2026-11-22 D-11 and D-12: 90 days of retention, in batches, and the export link goes away once it expires.</summary>
public sealed class InAppRetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InAppNotificationRetentionWorker WorkerOf(NotificationsApiFactory factory) =>
        factory.Services.GetServices<IHostedService>().OfType<InAppNotificationRetentionWorker>().Single();

    [Fact]
    public async Task PurgeDeletesOnlyWhatIsOlderThanTheRetention()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddDays(-91));
        var recent = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportFailed", """{"exportKind":"Orders"}""", now.AddDays(-89));

        var deleted = await WorkerOf(factory).PurgeAsync(now, Ct);

        Assert.Equal(1, deleted);
        Assert.Equal([recent], (await EntriesAsync(database.GetConnectionString(), tenantId)).Select(row => row.Id));
    }

    [Fact]
    public async Task PurgeDeletesInBatchesUntilDone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(Ct);
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO notifications.in_app_notifications (id, tenant_id, member_id, kind, data, dedupe_key, created_at)
                SELECT gen_random_uuid(), @tenant, @member, 'ExportFailed', '{}'::jsonb, 'old-' || n, now() - interval '120 days'
                FROM generate_series(1, 2500) AS n
                """,
                connection);
            seed.Parameters.AddWithValue("tenant", tenantId);
            seed.Parameters.AddWithValue("member", owner);
            await seed.ExecuteNonQueryAsync(Ct);
        }

        var deleted = await WorkerOf(factory).PurgeAsync(DateTimeOffset.UtcNow, Ct);

        Assert.Equal(2500, deleted);
        Assert.Empty(await EntriesAsync(database.GetConnectionString(), tenantId));
    }

    [Fact]
    public async Task PurgeScrubsExpiredDownloadUrls()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        string Data(DateTimeOffset expiresAt) => JsonSerializer.Serialize(new
        {
            exportKind = "Products",
            fileName = "x.xlsx",
            rowCount = 3,
            expiresAt = expiresAt.ToString("O"),
            downloadUrl = "https://r2.test/x.xlsx",
        });
        var expired = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportReady", Data(now.AddHours(-2)), now.AddHours(-26));
        var live = await InsertEntryAsync(database.GetConnectionString(), tenantId, owner, "ExportReady", Data(now.AddHours(5)), now.AddHours(-1));

        await WorkerOf(factory).PurgeAsync(now, Ct);

        var rows = (await EntriesAsync(database.GetConnectionString(), tenantId)).ToDictionary(row => row.Id);
        Assert.Equal(JsonValueKind.Null, rows[expired].Data.GetProperty("downloadUrl").ValueKind);
        Assert.Equal("x.xlsx", rows[expired].Data.GetProperty("fileName").GetString());
        Assert.Equal("https://r2.test/x.xlsx", rows[live].Data.GetProperty("downloadUrl").GetString());
    }
}
