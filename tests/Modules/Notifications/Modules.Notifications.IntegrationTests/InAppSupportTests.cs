using System.Text.Json;
using Modules.Notifications.Domain;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>Spec 2026-11-22 §6 and §12: the two Support events become entries of the bell.</summary>
public sealed class InAppSupportTests
{
    private const string Consumer = "notifications.in-app.support";
    private const string RepliedEvent = "support.ticket.customer-replied.v1";
    private const string UnassignedEvent = "support.ticket.unassigned-alert.v1";

    [Fact]
    public async Task CustomerRepliedCreatesAnEntryForTheAssignee()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var ticketId = Guid.CreateVersion7();
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(),
            RepliedEvent,
            JsonSerializer.Serialize(new
            {
                tenantId,
                ticketId,
                ticketNumber = "SOP-000127",
                assigneeMemberId = owner,
                occurredAt = DateTimeOffset.UtcNow,
            }));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(owner, entry.MemberId);
        Assert.Equal(nameof(InAppNotificationKind.SupportCustomerReplied), entry.Kind);
        Assert.Equal(ticketId, entry.ResourceId);
        Assert.Equal(ticketId, entry.Data.GetProperty("ticketId").GetGuid());
        Assert.Equal("SOP-000127", entry.Data.GetProperty("ticketNumber").GetString());
        Assert.Equal(messageId.ToString("D"), entry.DedupeKey);
        var notice = Assert.Single(publisher.Published);
        Assert.Equal((tenantId, owner, "notificationsChanged"), (notice.TenantId, notice.MemberId, notice.Kind));
    }

    [Fact]
    public async Task AnUnassignedAlertCreatesOneEntryPerRecipient()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var (second, _) = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Bruno Díaz");
        var ticketId = Guid.CreateVersion7();
        var since = new DateTimeOffset(2026, 11, 22, 13, 0, 0, TimeSpan.Zero);
        await InsertOutboxAsync(
            database.GetConnectionString(),
            UnassignedEvent,
            JsonSerializer.Serialize(new
            {
                tenantId,
                ticketId,
                ticketNumber = "SOP-000127",
                recipientMemberIds = new[] { owner, second },
                thresholdHours = 4,
                unassignedSince = since,
                occurredAt = DateTimeOffset.UtcNow,
            }));

        await DrainAsync(factory, Consumer);

        var entries = await EntriesAsync(database.GetConnectionString(), tenantId);
        Assert.Equal(2, entries.Count);
        Assert.Equivalent(new[] { owner, second }, entries.Select(entry => entry.MemberId));
        Assert.All(entries, entry =>
        {
            Assert.Equal(nameof(InAppNotificationKind.SupportTicketUnassigned), entry.Kind);
            Assert.Equal(ticketId, entry.ResourceId);
            Assert.Equal(4, entry.Data.GetProperty("thresholdHours").GetInt32());
            Assert.Equal("SOP-000127", entry.Data.GetProperty("ticketNumber").GetString());
            // One alert per ticket and stretch: the key names the moment it became unassigned, in UTC and the round-trip format.
            Assert.Equal($"support-unassigned:{ticketId:D}:{since:O}", entry.DedupeKey);
        });
        Assert.Equal(2, publisher.Published.Count);
    }

    [Fact]
    public async Task TheSameAlertPublishedTwiceForOneStretchAddsNoEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var ticketId = Guid.CreateVersion7();
        var since = new DateTimeOffset(2026, 11, 22, 13, 0, 0, TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        var payload = JsonSerializer.Serialize(new
        {
            tenantId,
            ticketId,
            ticketNumber = "SOP-000127",
            recipientMemberIds = new[] { owner },
            thresholdHours = 4,
            unassignedSince = since,
            occurredAt = now,
        });
        await InsertOutboxAsync(database.GetConnectionString(), UnassignedEvent, payload, now.AddMinutes(-10));
        await InsertOutboxAsync(database.GetConnectionString(), UnassignedEvent, payload, now.AddMinutes(-5));

        await DrainAsync(factory, Consumer);

        Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
    }
}
