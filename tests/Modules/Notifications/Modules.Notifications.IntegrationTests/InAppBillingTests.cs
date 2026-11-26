using System.Text.Json;
using Modules.Notifications.Domain;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// Spec 2026-11-22 §6 and D-17: «tienes N cobros vencidos», one entry per member and day; the price reviews of the service contracts
/// (spec reajuste §6.6) follow the same rule with their own entry.
/// </summary>
public sealed class InAppBillingTests
{
    private const string Consumer = "notifications.in-app.billing";
    private const string DigestEvent = "billing.charges.overdue-digest.v1";
    private const string PriceReviewEvent = "billing.price-reviews.digest.v1";

    private static string Payload(Guid tenantId, Guid memberId, string runDate, int overdueCount) =>
        JsonSerializer.Serialize(new { tenantId, memberId, runDate, overdueCount, occurredAt = DateTimeOffset.UtcNow });

    private static string PriceReviewPayload(Guid tenantId, Guid memberId, string runDate, int dueCount, int heldCount) =>
        JsonSerializer.Serialize(new { tenantId, memberId, runDate, dueCount, heldCount, occurredAt = DateTimeOffset.UtcNow });

    [Fact]
    public async Task APriceReviewEventCreatesAnEntryForTheMember()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), PriceReviewEvent, PriceReviewPayload(tenantId, owner, "2026-03-10", 2, 1));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(owner, entry.MemberId);
        Assert.Equal("ServicePriceReviewsDue", entry.Kind);
        Assert.Equal(nameof(InAppNotificationKind.ServicePriceReviewsDue), entry.Kind);
        Assert.Null(entry.ResourceId);
        Assert.Equal(2, entry.Data.GetProperty("dueCount").GetInt32());
        Assert.Equal(1, entry.Data.GetProperty("heldCount").GetInt32());
        Assert.Equal("2026-03-10", entry.Data.GetProperty("runDate").GetString());
        Assert.Equal("billing-price-review:2026-03-10", entry.DedupeKey);
        Assert.Equal(messageId, entry.SourceMessageId);
        var notice = Assert.Single(publisher.Published);
        Assert.Equal((tenantId, owner, "notificationsChanged"), (notice.TenantId, notice.MemberId, notice.Kind));
        Assert.Equal(entry.Id, notice.Payload.GetProperty("notificationId").GetGuid());
    }

    [Fact]
    public async Task ARepeatedPriceReviewEventTheSameDayDoesNotDuplicate()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        var first = await InsertOutboxAsync(
            database.GetConnectionString(), PriceReviewEvent, PriceReviewPayload(tenantId, owner, "2026-03-10", 2, 1),
            now.AddMinutes(-10));
        var second = await InsertOutboxAsync(
            database.GetConnectionString(), PriceReviewEvent, PriceReviewPayload(tenantId, owner, "2026-03-10", 2, 1),
            now.AddMinutes(-5));

        await DrainAsync(factory, Consumer);

        Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Single(publisher.Published);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, first))!.ProcessedAt);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, second))!.ProcessedAt);
    }

    // Las dos campanas del día tienen claves distintas: una no se come a la otra.
    [Fact]
    public async Task OverdueAndPriceReviewEntriesOfTheSameDayCoexist()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        await InsertOutboxAsync(
            database.GetConnectionString(), DigestEvent, Payload(tenantId, owner, "2026-03-10", 3), now.AddMinutes(-10));
        await InsertOutboxAsync(
            database.GetConnectionString(), PriceReviewEvent, PriceReviewPayload(tenantId, owner, "2026-03-10", 2, 1),
            now.AddMinutes(-5));

        await DrainAsync(factory, Consumer);

        var entries = await EntriesAsync(database.GetConnectionString(), tenantId);
        Assert.Equal(
            ["billing-overdue:2026-03-10", "billing-price-review:2026-03-10"], entries.Select(entry => entry.DedupeKey).Order());
    }

    [Fact]
    public async Task ADigestEventCreatesAnOverdueEntryForTheMember()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var messageId = await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, Payload(tenantId, owner, "2026-03-10", 3));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(owner, entry.MemberId);
        Assert.Equal(nameof(InAppNotificationKind.ServiceChargesOverdue), entry.Kind);
        Assert.Null(entry.ResourceId);
        Assert.Equal(3, entry.Data.GetProperty("overdueCount").GetInt32());
        Assert.Equal("2026-03-10", entry.Data.GetProperty("runDate").GetString());
        // The day, not the message, is the key: the producer may publish the same day twice.
        Assert.Equal("billing-overdue:2026-03-10", entry.DedupeKey);
        Assert.Equal(messageId, entry.SourceMessageId);
        var notice = Assert.Single(publisher.Published);
        Assert.Equal((tenantId, owner, "notificationsChanged"), (notice.TenantId, notice.MemberId, notice.Kind));
        Assert.Equal(entry.Id, notice.Payload.GetProperty("notificationId").GetGuid());
    }

    [Fact]
    public async Task ARetakenClaimDoesNotDuplicateTheEntry()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        var first = await InsertOutboxAsync(
            database.GetConnectionString(), DigestEvent, Payload(tenantId, owner, "2026-03-10", 3), now.AddMinutes(-10));
        // The same day published again after a claim was retaken: another message, the same entry.
        var second = await InsertOutboxAsync(
            database.GetConnectionString(), DigestEvent, Payload(tenantId, owner, "2026-03-10", 3), now.AddMinutes(-5));

        await DrainAsync(factory, Consumer);

        Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Single(publisher.Published);
        // Both messages are processed: the duplicate is absorbed by the entry, not retried.
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, first))!.ProcessedAt);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, second))!.ProcessedAt);
    }

    [Fact]
    public async Task ADifferentDayCreatesAnotherEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, Payload(tenantId, owner, "2026-03-09", 2), now.AddMinutes(-10));
        await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, Payload(tenantId, owner, "2026-03-10", 3), now.AddMinutes(-5));

        await DrainAsync(factory, Consumer);

        var entries = await EntriesAsync(database.GetConnectionString(), tenantId);
        Assert.Equal(["billing-overdue:2026-03-09", "billing-overdue:2026-03-10"], entries.Select(entry => entry.DedupeKey).Order());
    }
}
