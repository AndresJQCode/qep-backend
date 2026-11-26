using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.InApp;
using Modules.Notifications.Infrastructure.Persistence;
using Npgsql;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>Spec 2026-11-22 §6, D-5 to D-7: the exports reach the bell of the membership that asked for them, once.</summary>
public sealed class InAppExportsTests
{
    private const string Consumer = "notifications.in-app.exports";

    [Fact]
    public async Task AProductExportReadyCreatesAnEntryForTheRequesterMembership()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var memberId = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(24);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(),
            "catalog.product-export-ready.v1",
            JsonSerializer.Serialize(new
            {
                tenantId,
                subjectId = ownerUserId,
                downloadUrl = "https://r2.test/exports/productos.xlsx?X-Amz-Signature=abc",
                fileName = "productos.xlsx",
                productCount = 7,
                expiresAt,
            }));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(memberId, entry.MemberId);
        Assert.Equal(nameof(InAppNotificationKind.ExportReady), entry.Kind);
        Assert.Equal("Products", entry.Data.GetProperty("exportKind").GetString());
        Assert.Equal("productos.xlsx", entry.Data.GetProperty("fileName").GetString());
        Assert.Equal(7, entry.Data.GetProperty("rowCount").GetInt32());
        Assert.Equal("https://r2.test/exports/productos.xlsx?X-Amz-Signature=abc", entry.Data.GetProperty("downloadUrl").GetString());
        Assert.Equal(expiresAt, entry.Data.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(messageId.ToString("D"), entry.DedupeKey);
        Assert.Equal(messageId, entry.SourceMessageId);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, messageId))!.ProcessedAt);
    }

    // The warehouse stock export travels under its own event: the payload names its count rowCount and also carries the fileId, which
    // is not part of the bell's data.
    [Fact]
    public async Task AWmsStockExportReadyCreatesAStockEntryForTheRequesterMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var memberId = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(24);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(),
            "wms.export-ready.v1",
            JsonSerializer.Serialize(new
            {
                tenantId,
                subjectId = ownerUserId,
                fileId = Guid.CreateVersion7(),
                downloadUrl = "https://r2.test/exports/saldos.xlsx?X-Amz-Signature=abc",
                fileName = "saldos.xlsx",
                rowCount = 12,
                expiresAt,
            }));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(memberId, entry.MemberId);
        Assert.Equal(nameof(InAppNotificationKind.ExportReady), entry.Kind);
        Assert.Equal(
            ["downloadUrl", "expiresAt", "exportKind", "fileName", "rowCount"],
            entry.Data.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Stock", entry.Data.GetProperty("exportKind").GetString());
        Assert.Equal("saldos.xlsx", entry.Data.GetProperty("fileName").GetString());
        Assert.Equal(12, entry.Data.GetProperty("rowCount").GetInt32());
        Assert.Equal("https://r2.test/exports/saldos.xlsx?X-Amz-Signature=abc", entry.Data.GetProperty("downloadUrl").GetString());
        Assert.Equal(expiresAt, entry.Data.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(messageId.ToString("D"), entry.DedupeKey);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, messageId))!.ProcessedAt);
    }

    [Fact]
    public async Task AnExportOfAUserWithoutMembershipLeavesNoEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, _, _) = await RegisterOwnerAsync(factory);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(),
            "customers.export-ready.v1",
            JsonSerializer.Serialize(new
            {
                tenantId,
                subjectId = Guid.CreateVersion7(),
                downloadUrl = "https://r2.test/x",
                fileName = "clientes.xlsx",
                customerCount = 3,
                expiresAt = DateTimeOffset.UtcNow.AddHours(24),
            }));

        await DrainAsync(factory, Consumer);

        Assert.Empty(await EntriesAsync(database.GetConnectionString(), tenantId));
        // The message is still processed: with no membership there is nothing to retry.
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, messageId))!.ProcessedAt);
    }

    [Fact]
    public async Task AQuotationsExportFailedCreatesAnExportFailedEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-failed.v1",
            JsonSerializer.Serialize(new { tenantId, subjectId = ownerUserId, kind = "Orders" }));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(nameof(InAppNotificationKind.ExportFailed), entry.Kind);
        Assert.Equal("Orders", entry.Data.GetProperty("exportKind").GetString());
        Assert.False(entry.Data.TryGetProperty("downloadUrl", out _));
    }

    // Spec D-6: the outbox already holds history. A consumer born today must not fill every bell with it.
    [Fact]
    public async Task EventsOlderThanTheMaxAgeAreNeverClaimed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-failed.v1",
            JsonSerializer.Serialize(new { tenantId, subjectId = ownerUserId, kind = "Orders" }),
            DateTimeOffset.UtcNow.AddHours(-25));

        await DrainAsync(factory, Consumer);

        Assert.Null(await FindInboxAsync(database.GetConnectionString(), Consumer, messageId));
        Assert.Empty(await EntriesAsync(database.GetConnectionString(), tenantId));
    }

    [Fact]
    public async Task ProcessingTheSameMessageTwiceLeavesOneEntry()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-failed.v1",
            JsonSerializer.Serialize(new { tenantId, subjectId = ownerUserId, kind = "Quotations" }));
        await DrainAsync(factory, Consumer);

        // A second claim of the same message (lost lease, other pod): the unique dedupe key absorbs it.
        await PutInboxAsync(database.GetConnectionString(), Consumer, messageId, processedAt: null, claimedUntil: DateTimeOffset.UtcNow.AddMinutes(-1), attempts: 1);
        await DrainAsync(factory, Consumer);

        Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Single(publisher.Published);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, messageId))!.ProcessedAt);
    }

    [Fact]
    public async Task ANewEntryIsPublishedToTheMemberAfterCommit()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var memberId = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        await InsertOutboxAsync(
            database.GetConnectionString(),
            "quotations.export-failed.v1",
            JsonSerializer.Serialize(new { tenantId, subjectId = ownerUserId, kind = "Orders" }));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        var notice = Assert.Single(publisher.Published);
        Assert.Equal((tenantId, memberId, "notificationsChanged"), (notice.TenantId, notice.MemberId, notice.Kind));
        Assert.Equal(entry.Id, notice.Payload.GetProperty("notificationId").GetGuid());
    }

    // Spec D-7: one message with three recipients where one already has its entry. A 23505 inside a SaveChanges would revert the
    // other two and the message would retry until MaxAttempts without writing anything.
    [Fact]
    public async Task APartiallyDuplicatedBatchKeepsTheNewEntriesAndFinishes()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, _, _) = await RegisterOwnerAsync(factory);
        var messageId = Guid.CreateVersion7();
        var members = new[] { Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7() };
        var now = DateTimeOffset.UtcNow;
        InAppNotification NewEntry(Guid member) => InAppNotification.Create(
            tenantId, member, InAppNotificationKind.SupportTicketUnassigned, Guid.CreateVersion7(), "{}", "same-key", messageId, now);
        await using (var connection = new NpgsqlConnection(database.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO notifications.in_app_notifications (id, tenant_id, member_id, kind, data, dedupe_key, created_at)
                VALUES (@id, @tenant, @member, 'SupportTicketUnassigned', '{}'::jsonb, 'same-key', now())
                """,
                connection);
            seed.Parameters.AddWithValue("id", Guid.CreateVersion7());
            seed.Parameters.AddWithValue("tenant", tenantId);
            seed.Parameters.AddWithValue("member", members[0]);
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        await InboxClaims.TryClaimAsync(db, "test.consumer", messageId, now, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        var inserted = await InAppNotificationWriter.SaveAsync(
            db, "test.consumer", messageId, [.. members.Select(NewEntry)], now, publisher, TestContext.Current.CancellationToken);

        Assert.Equal(2, inserted);
        Assert.Equal(3, (await EntriesAsync(database.GetConnectionString(), tenantId)).Count);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), "test.consumer", messageId))!.ProcessedAt);
        Assert.Equal(2, publisher.Published.Count);
        Assert.DoesNotContain(publisher.Published, notice => notice.MemberId == members[0]);
    }
}
