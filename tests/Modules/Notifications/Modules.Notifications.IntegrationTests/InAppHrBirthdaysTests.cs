using Modules.Notifications.Domain;
using static Modules.Notifications.IntegrationTests.HrBirthdayTestSupport;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// HR birthday notices, spec 2026-11-26 §5.3: <c>hr.birthdays.digest.v1</c> becomes one bell entry per manager and day and, in the same
/// transaction, one delivery row per enabled channel. Hr may publish the same digest twice after a crash: the bell dedupe key and the
/// delivery unique key make the replay change nothing. Names only: the entries never keep a birth date or an age.
/// </summary>
public sealed class InAppHrBirthdaysTests
{
    private const string Consumer = "notifications.in-app.hr";

    [Fact]
    public async Task DigestWritesOneBellEntryPerManagerAndDay()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var (other, _) = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Marta Díaz");
        var ana = new Person(Guid.CreateVersion7(), "Ana Pérez");
        var luis = new Person(Guid.CreateVersion7(), "Luis Gómez");
        var messageId = await InsertOutboxAsync(
            database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, owner, today: [ana], upcoming: [luis], todayTotal: 1, upcomingTotal: 4));
        await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, other, today: [ana], upcoming: [luis]));

        await DrainAsync(factory, Consumer);

        var entries = await EntriesAsync(database.GetConnectionString(), tenantId);
        Assert.Equal(2, entries.Count);
        var entry = Assert.Single(entries, row => row.MemberId == owner);
        Assert.Equal(nameof(InAppNotificationKind.HrBirthdays), entry.Kind);
        Assert.Null(entry.ResourceId);
        Assert.Equal("hr-birthdays:2026-11-26", entry.DedupeKey);
        Assert.Equal(messageId, entry.SourceMessageId);
        Assert.Equal("2026-11-26", entry.Data.GetProperty("runDate").GetString());
        Assert.Equal("2026-11-27", entry.Data.GetProperty("upcomingDate").GetString());
        Assert.Equal((1, 4), (entry.Data.GetProperty("todayTotal").GetInt32(), entry.Data.GetProperty("upcomingTotal").GetInt32()));
        Assert.Equal(ana.EmployeeId, Assert.Single(entry.Data.GetProperty("today").EnumerateArray()).GetProperty("employeeId").GetGuid());
        Assert.Equal("Luis Gómez", Assert.Single(entry.Data.GetProperty("upcoming").EnumerateArray()).GetProperty("employeeName").GetString());
        Assert.Equal(2, publisher.Published.Count);

        // Another day is another entry for the same manager.
        await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, owner, runDate: "2026-11-27"));
        await DrainAsync(factory, Consumer);
        Assert.Equal(3, (await EntriesAsync(database.GetConnectionString(), tenantId)).Count);
    }

    [Fact]
    public async Task DigestWritesOneDeliveryPerEnabledChannel()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var (other, _) = await SeedMemberAsync(database.GetConnectionString(), tenantId, "Marta Díaz");
        var connectionId = Guid.CreateVersion7();
        var author = Guid.CreateVersion7();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(10);
        await InsertOutboxAsync(
            database.GetConnectionString(), DigestEvent,
            DigestPayload(tenantId, owner, whatsAppConnectionId: connectionId, email: true, sentByMemberId: author, expiresAt: expiresAt));
        await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, other, whatsAppConnectionId: null, email: true));

        await DrainAsync(factory, Consumer);

        var rows = await DeliveriesAsync(database.GetConnectionString(), tenantId);
        Assert.Equal(3, rows.Count);
        var whatsapp = Assert.Single(rows, row => row.Channel == "whatsapp");
        Assert.Equal((owner, connectionId, author, "Pending", 0, "2026-11-26"), (
            whatsapp.MemberId, whatsapp.ConnectionId, whatsapp.SentByMemberId, whatsapp.Status, whatsapp.Attempts, whatsapp.RunDate));
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), whatsapp.ExpiresAt.ToUnixTimeSeconds());
        var ownerEmail = Assert.Single(rows, row => row.Channel == "email" && row.MemberId == owner);
        Assert.Null(ownerEmail.ConnectionId);
        // Email only: the second manager has no WhatsApp row.
        var otherRows = rows.Where(row => row.MemberId == other).ToArray();
        Assert.Equal("email", Assert.Single(otherRows).Channel);
        Assert.Equal(other, otherRows[0].SentByMemberId);
    }

    [Fact]
    public async Task ReplayedDigestChangesNothing()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var connectionId = Guid.CreateVersion7();
        var payload = DigestPayload(tenantId, owner, whatsAppConnectionId: connectionId, email: true);
        var now = DateTimeOffset.UtcNow;
        await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, payload, now.AddMinutes(-10));
        await DrainAsync(factory, Consumer);
        var first = await DeliveriesAsync(database.GetConnectionString(), tenantId);
        // One of them already went out: a replay must not put it back to Pending.
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE notifications.hr_birthday_deliveries SET status = 'Sent', attempts = 1, completed_at = now() WHERE channel = 'whatsapp' AND tenant_id = @t",
            ("t", tenantId));

        // Hr republished the same digest after a crash (a new outbox message), even with different channels.
        var replay = DigestPayload(tenantId, owner, whatsAppConnectionId: null, email: false);
        await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, replay, now.AddMinutes(-5));
        await DrainAsync(factory, Consumer);

        Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Single(publisher.Published);
        var after = await DeliveriesAsync(database.GetConnectionString(), tenantId);
        Assert.Equal(first.Select(row => row.Id).Order(), after.Select(row => row.Id).Order());
        Assert.Equal(2, after.Count);
        var whatsapp = Assert.Single(after, row => row.Channel == "whatsapp");
        Assert.Equal(("Sent", 1, connectionId), (whatsapp.Status, whatsapp.Attempts, whatsapp.ConnectionId));
        Assert.Equal("Pending", Assert.Single(after, row => row.Channel == "email").Status);
    }

    [Fact]
    public async Task BellDataHasNoBirthDate()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        // The payload under test carries a birth date and an age on purpose: whatever else it carries, the consumer keeps only id and name.
        await InsertOutboxAsync(
            database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, owner, whatsAppConnectionId: Guid.CreateVersion7(), email: true));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        var person = entry.Data.GetProperty("today").EnumerateArray().Single();
        Assert.Equal(["employeeId", "employeeName"], person.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("birth", entry.Data.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1990", entry.Data.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("age", entry.Data.GetRawText(), StringComparison.Ordinal);
        foreach (var row in await DeliveriesAsync(database.GetConnectionString(), tenantId))
        {
            Assert.DoesNotContain("birth", row.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("1990", row.Payload, StringComparison.Ordinal);
        }
    }
}
