using System.Text.Json;
using Modules.Notifications.Domain;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>Spec 2026-11-22 §6 and D-13: a wholesale order awaiting approval opens an entry for its advisor only; its approval or
/// cancellation closes it, and a cancellation after an approval does not overwrite it.</summary>
public sealed class InAppWholesaleTests
{
    private const string Consumer = "notifications.in-app.wholesale";
    private const string Created = "quotations.order.created.v1";
    private const string Approved = "quotations.order.wholesale-approved.v1";
    private const string Cancelled = "quotations.order.wholesale-cancelled.v1";

    private static string CreatedPayload(Guid tenantId, Guid orderId, Guid advisorMemberId, string source = "Wholesale") =>
        JsonSerializer.Serialize(new
        {
            tenantId,
            orderId,
            orderNumber = "PED-000042",
            clientId = Guid.CreateVersion7(),
            advisorMemberId,
            total = 150000m,
            currency = "COP",
            source,
            quotationId = (Guid?)null,
            occurredAt = DateTimeOffset.UtcNow,
        });

    private static string ApprovedPayload(Guid tenantId, Guid orderId) =>
        JsonSerializer.Serialize(new
        {
            tenantId,
            orderId,
            orderNumber = "PED-000042",
            buyerTenantId = Guid.CreateVersion7(),
            purchaseOrderId = Guid.CreateVersion7(),
            currency = "COP",
            approvedAt = DateTimeOffset.UtcNow,
            lines = Array.Empty<object>(),
        });

    private static string CancelledPayload(Guid tenantId, Guid orderId) =>
        JsonSerializer.Serialize(new
        {
            tenantId,
            orderId,
            buyerTenantId = Guid.CreateVersion7(),
            purchaseOrderId = Guid.CreateVersion7(),
            cancelledAt = DateTimeOffset.UtcNow,
            byBuyer = false,
        });

    [Fact]
    public async Task AWholesaleOrderNotifiesItsAdvisorOnly()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var advisor = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Beatriz");
        var orderId = Guid.CreateVersion7();
        var messageId = await InsertOutboxAsync(database.GetConnectionString(), Created, CreatedPayload(tenantId, orderId, advisor.MemberId));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(advisor.MemberId, entry.MemberId);
        Assert.NotEqual(owner, entry.MemberId);
        Assert.Equal(nameof(InAppNotificationKind.WholesaleOrderPendingApproval), entry.Kind);
        Assert.Equal(orderId, entry.ResourceId);
        Assert.Equal(orderId, entry.Data.GetProperty("orderId").GetGuid());
        Assert.Equal("PED-000042", entry.Data.GetProperty("orderNumber").GetString());
        Assert.Equal(JsonValueKind.Null, entry.Data.GetProperty("resolution").ValueKind);
        Assert.Equal(messageId.ToString("D"), entry.DedupeKey);
        Assert.Null(entry.ResolvedAt);
        Assert.Null(entry.ReadAt);
        var notice = Assert.Single(publisher.Published);
        Assert.Equal((tenantId, advisor.MemberId, "notificationsChanged"), (notice.TenantId, notice.MemberId, notice.Kind));
        Assert.Equal(entry.Id, notice.Payload.GetProperty("notificationId").GetGuid());
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("Shopify")]
    public async Task AManualOrderLeavesNoEntry(string source)
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), Created, CreatedPayload(tenantId, Guid.CreateVersion7(), owner, source));

        await DrainAsync(factory, Consumer);

        Assert.Empty(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Empty(publisher.Published);
        // Nothing to notify is still a processed message: it is not retried.
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, messageId))!.ProcessedAt);
    }

    [Fact]
    public async Task ApprovalResolvesAndReadsTheEntry()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var orderId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await InsertOutboxAsync(database.GetConnectionString(), Created, CreatedPayload(tenantId, orderId, owner), now.AddSeconds(-2));
        await InsertOutboxAsync(database.GetConnectionString(), Approved, ApprovedPayload(tenantId, orderId), now.AddSeconds(-1));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal("Approved", entry.Data.GetProperty("resolution").GetString());
        Assert.NotNull(entry.ResolvedAt);
        Assert.NotNull(entry.ReadAt);
        // The entry was announced when it was born and again when it was resolved, so the other tabs update the badge.
        Assert.Equal(2, publisher.Published.Count(notice => notice.MemberId == owner && notice.Payload.GetProperty("notificationId").GetGuid() == entry.Id));
    }

    [Fact]
    public async Task CancellationResolvesTheEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var orderId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await InsertOutboxAsync(database.GetConnectionString(), Created, CreatedPayload(tenantId, orderId, owner), now.AddSeconds(-2));
        await InsertOutboxAsync(database.GetConnectionString(), Cancelled, CancelledPayload(tenantId, orderId), now.AddSeconds(-1));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal("Cancelled", entry.Data.GetProperty("resolution").GetString());
        Assert.NotNull(entry.ResolvedAt);
        Assert.NotNull(entry.ReadAt);
    }

    [Fact]
    public async Task ApprovalWithoutEntryDoesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, _, _) = await RegisterOwnerAsync(factory);
        var messageId = await InsertOutboxAsync(database.GetConnectionString(), Approved, ApprovedPayload(tenantId, Guid.CreateVersion7()));

        await DrainAsync(factory, Consumer);

        Assert.Empty(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Empty(publisher.Published);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, messageId))!.ProcessedAt);
    }

    // Spec D-13: CancelOrder also publishes wholesale-cancelled for an order that was already approved; that must not overwrite «Approved».
    [Fact]
    public async Task ACancelAfterApprovalKeepsApproved()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var orderId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await InsertOutboxAsync(database.GetConnectionString(), Created, CreatedPayload(tenantId, orderId, owner), now.AddSeconds(-3));
        await InsertOutboxAsync(database.GetConnectionString(), Approved, ApprovedPayload(tenantId, orderId), now.AddSeconds(-2));
        await DrainAsync(factory, Consumer);
        var afterApproval = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        var announced = publisher.Published.Count;
        await InsertOutboxAsync(database.GetConnectionString(), Cancelled, CancelledPayload(tenantId, orderId), now.AddSeconds(-1));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal("Approved", entry.Data.GetProperty("resolution").GetString());
        Assert.Equal(afterApproval.ResolvedAt, entry.ResolvedAt);
        Assert.Equal(afterApproval.ReadAt, entry.ReadAt);
        // Nothing changed, so nothing is announced.
        Assert.Equal(announced, publisher.Published.Count);
    }
}
