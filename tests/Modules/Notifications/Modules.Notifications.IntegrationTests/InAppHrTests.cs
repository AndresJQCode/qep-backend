using System.Text.Json;
using Modules.Notifications.Domain;
using static Modules.Notifications.IntegrationTests.InAppTestSupport;
using static Modules.Notifications.IntegrationTests.NotificationsDeliveryHarness;

namespace Modules.Notifications.IntegrationTests;

/// <summary>
/// Spec 2026-11-23 §9.3 and 2026-11-22 §6: «tienes N exámenes médicos pendientes» (one entry per manager and day) and «salió
/// {empleado}: egreso pendiente» (one per termination). The payloads carry no clinical text and no exam type, and neither do the entries.
/// </summary>
public sealed class InAppHrTests
{
    private const string Consumer = "notifications.in-app.hr";
    private const string DigestEvent = "hr.medical-exams.due-digest.v1";
    private const string ExitEvent = "hr.medical-exams.exit-required.v1";

    private static string DigestPayload(Guid tenantId, Guid memberId, string runDate, int pendingCount) =>
        JsonSerializer.Serialize(new { tenantId, memberId, runDate, pendingCount, occurredAt = DateTimeOffset.UtcNow });

    private static string ExitPayload(Guid tenantId, Guid memberId, Guid employeeId, string employeeName, string dueDate) =>
        JsonSerializer.Serialize(new { tenantId, memberId, employeeId, employeeName, dueDate, occurredAt = DateTimeOffset.UtcNow });

    [Fact]
    public async Task ConsumerMapsDueDigestToHrMedicalExamsDue()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var messageId = await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, owner, "2026-11-20", 7));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(owner, entry.MemberId);
        Assert.Equal(nameof(InAppNotificationKind.HrMedicalExamsDue), entry.Kind);
        Assert.Null(entry.ResourceId);
        Assert.Equal(7, entry.Data.GetProperty("pendingCount").GetInt32());
        Assert.Equal("2026-11-20", entry.Data.GetProperty("runDate").GetString());
        Assert.Equal("hr-medical-exams:2026-11-20", entry.DedupeKey);
        Assert.Equal(messageId, entry.SourceMessageId);
        var notice = Assert.Single(publisher.Published);
        Assert.Equal((tenantId, owner, "notificationsChanged"), (notice.TenantId, notice.MemberId, notice.Kind));
    }

    [Fact]
    public async Task ConsumerMapsExitRequiredToHrExitExamRequired()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var employeeId = Guid.CreateVersion7();
        await InsertOutboxAsync(database.GetConnectionString(), ExitEvent, ExitPayload(tenantId, owner, employeeId, "Ana Pérez", "2026-12-07"));

        await DrainAsync(factory, Consumer);

        var entry = Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Equal(owner, entry.MemberId);
        Assert.Equal(nameof(InAppNotificationKind.HrExitExamRequired), entry.Kind);
        Assert.Equal(employeeId, entry.ResourceId);
        Assert.Equal(employeeId, entry.Data.GetProperty("employeeId").GetGuid());
        Assert.Equal("Ana Pérez", entry.Data.GetProperty("employeeName").GetString());
        Assert.Equal("2026-12-07", entry.Data.GetProperty("dueDate").GetString());
        Assert.Equal($"hr-exit-exam:{employeeId}:2026-12-07", entry.DedupeKey);
    }

    [Fact]
    public async Task ConsumerDedupesTheSameRunDate()
    {
        await using var database = await StartDatabaseAsync();
        var publisher = new RecordingMemberPublisher();
        using var factory = NewFactory(database.GetConnectionString(), publisher);
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var now = DateTimeOffset.UtcNow;
        var first = await InsertOutboxAsync(
            database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, owner, "2026-11-20", 7), now.AddMinutes(-10));
        // The same day published again after a claim was retaken: another message, the same entry.
        var second = await InsertOutboxAsync(
            database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, owner, "2026-11-20", 7), now.AddMinutes(-5));

        await DrainAsync(factory, Consumer);

        Assert.Single(await EntriesAsync(database.GetConnectionString(), tenantId));
        Assert.Single(publisher.Published);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, first))!.ProcessedAt);
        Assert.NotNull((await FindInboxAsync(database.GetConnectionString(), Consumer, second))!.ProcessedAt);

        // Another day is another entry.
        await InsertOutboxAsync(database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, owner, "2026-11-21", 5));
        await DrainAsync(factory, Consumer);
        Assert.Equal(2, (await EntriesAsync(database.GetConnectionString(), tenantId)).Count);
    }

    [Fact]
    public async Task ConsumerDropsEventsOlderThan24h()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = NewFactory(database.GetConnectionString(), new RecordingMemberPublisher());
        var (tenantId, ownerUserId, _) = await RegisterOwnerAsync(factory);
        var owner = await MemberIdOfAsync(database.GetConnectionString(), tenantId, ownerUserId);
        var digest = await InsertOutboxAsync(
            database.GetConnectionString(), DigestEvent, DigestPayload(tenantId, owner, "2026-11-19", 4), DateTimeOffset.UtcNow.AddHours(-25));
        var exit = await InsertOutboxAsync(
            database.GetConnectionString(), ExitEvent, ExitPayload(tenantId, owner, Guid.CreateVersion7(), "Ana Pérez", "2026-11-19"),
            DateTimeOffset.UtcNow.AddHours(-25));

        await DrainAsync(factory, Consumer);

        Assert.Null(await FindInboxAsync(database.GetConnectionString(), Consumer, digest));
        Assert.Null(await FindInboxAsync(database.GetConnectionString(), Consumer, exit));
        Assert.Empty(await EntriesAsync(database.GetConnectionString(), tenantId));
    }
}
