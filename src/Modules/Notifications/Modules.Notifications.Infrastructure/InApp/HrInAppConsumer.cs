using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Notifications.Domain;
using Modules.Notifications.Infrastructure.Persistence;

namespace Modules.Notifications.Infrastructure.InApp;

/// <summary>
/// Spec 2026-11-23 §9.3: the two bell entries of the occupational medical exams of Hr, for whoever holds <c>hr.medical.manage</c> (the
/// producer already chose the recipient and writes one message per member):
/// <list type="bullet">
/// <item><c>hr.medical-exams.due-digest.v1</c> becomes <see cref="InAppNotificationKind.HrMedicalExamsDue"/>, one entry per member and
/// day. The producer may publish it twice for the same day (a claim retaken after its lease expired), so the key is the day and not the
/// message: the second one finds the entry already written and changes nothing.</item>
/// <item><c>hr.medical-exams.exit-required.v1</c> becomes <see cref="InAppNotificationKind.HrExitExamRequired"/>, one per employee and
/// deadline, with the employee as the resource.</item>
/// </list>
/// The payloads carry no clinical text and no exam type, and the entries only keep what the screen draws.
/// <para>HR birthday notices (spec 2026-11-26 §5.3): <c>hr.birthdays.digest.v1</c> becomes <see cref="InAppNotificationKind.HrBirthdays"/>, one
/// entry per manager and day (key <c>hr-birthdays:{runDate}</c>), and, in the same transaction, one <see cref="HrBirthdayDelivery"/> row per
/// enabled channel. Hr may republish the same digest after a crash: the entry is then not new, so no rows are written from it, and the rows'
/// unique key (<c>ON CONFLICT DO NOTHING</c>) covers the case where the entry exists but a row does not. Only ids and names are kept: never a
/// birth date or an age, whatever the payload carries.</para>
/// </summary>
internal sealed class HrInAppConsumer(IServiceScopeFactory scopeFactory, ILogger<HrInAppConsumer> logger)
    : InAppOutboxConsumer(scopeFactory, logger)
{
    internal const string DueDigestEvent = "hr.medical-exams.due-digest.v1";

    internal const string ExitRequiredEvent = "hr.medical-exams.exit-required.v1";

    internal const string BirthdayDigestEvent = "hr.birthdays.digest.v1";

    protected override string Consumer => "notifications.in-app.hr";

    protected override IReadOnlyCollection<string> EventNames => [DueDigestEvent, ExitRequiredEvent, BirthdayDigestEvent];

    protected override Task<IReadOnlyList<InAppNotification>> BuildAsync(
        OutboxRecord record, InAppContext context, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        var tenantId = root.GetProperty("tenantId").GetGuid();
        var memberId = root.GetProperty("memberId").GetGuid();

        InAppNotification entry;
        if (record.EventName == BirthdayDigestEvent)
        {
            var runDate = root.GetProperty("runDate").GetString() ?? string.Empty;
            var parts = BirthdayParts(root);
            entry = InAppNotification.Create(
                tenantId,
                memberId,
                InAppNotificationKind.HrBirthdays,
                resourceId: null,
                JsonSerializer.Serialize(new
                {
                    runDate,
                    parts.Today,
                    parts.TodayTotal,
                    parts.Upcoming,
                    parts.UpcomingTotal,
                    parts.UpcomingDate,
                }, BirthdayJson),
                dedupeKey: "hr-birthdays:" + runDate,
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow);
        }
        else if (record.EventName == DueDigestEvent)
        {
            var runDate = root.GetProperty("runDate").GetString() ?? string.Empty;
            entry = InAppNotification.Create(
                tenantId,
                memberId,
                InAppNotificationKind.HrMedicalExamsDue,
                resourceId: null,
                JsonSerializer.Serialize(new { pendingCount = root.GetProperty("pendingCount").GetInt32(), runDate }),
                dedupeKey: "hr-medical-exams:" + runDate,
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow);
        }
        else
        {
            var employeeId = root.GetProperty("employeeId").GetGuid();
            var dueDate = root.GetProperty("dueDate").GetString() ?? string.Empty;
            entry = InAppNotification.Create(
                tenantId,
                memberId,
                InAppNotificationKind.HrExitExamRequired,
                resourceId: employeeId,
                JsonSerializer.Serialize(new { employeeId, employeeName = root.GetProperty("employeeName").GetString(), dueDate }),
                dedupeKey: $"hr-exit-exam:{employeeId}:{dueDate}",
                sourceMessageId: record.Id,
                createdAt: context.Clock.UtcNow);
        }

        IReadOnlyList<InAppNotification> entries = [entry];
        return Task.FromResult(entries);
    }

    protected override async Task OnInsertedAsync(
        NotificationsDbContext dbContext,
        OutboxRecord record,
        IReadOnlyList<InAppNotification> inserted,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (record.EventName != BirthdayDigestEvent || inserted.Count == 0)
        {
            return;
        }

        using var document = JsonDocument.Parse(record.PayloadJson);
        var root = document.RootElement;
        var tenantId = root.GetProperty("tenantId").GetGuid();
        var memberId = root.GetProperty("memberId").GetGuid();
        var runDate = DateOnly.ParseExact(root.GetProperty("runDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var channels = root.GetProperty("channels");
        var sentBy = channels.GetProperty("sentByMemberId").GetGuid();
        // A DateTimeOffset, never a text comparison: Hr's serializer may write "Z" or "+00:00".
        var expiresAt = root.GetProperty("expiresAt").GetDateTimeOffset().ToUniversalTime();
        var created = now.ToUniversalTime();
        var parts = BirthdayParts(root);
        var payload = JsonSerializer.Serialize(parts, BirthdayJson);

        // The rows go with the entry that was really new; a replay finds none. ON CONFLICT keeps even that idempotent.
        if (channels.TryGetProperty("whatsAppConnectionId", out var connection) && connection.ValueKind == JsonValueKind.String
            && connection.TryGetGuid(out var connectionId))
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO notifications.hr_birthday_deliveries
                    (id, tenant_id, member_id, run_date, channel, connection_id, sent_by_member_id, payload, status, attempts, expires_at, created_at)
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {memberId}, {runDate}, {HrBirthdayDeliveryChannels.WhatsApp}, {connectionId}, {sentBy},
                        CAST({payload} AS jsonb), 'Pending', 0, {expiresAt}, {created})
                ON CONFLICT (tenant_id, member_id, run_date, channel) DO NOTHING
                """,
                CancellationToken.None);
        }

        if (channels.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.True)
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO notifications.hr_birthday_deliveries
                    (id, tenant_id, member_id, run_date, channel, connection_id, sent_by_member_id, payload, status, attempts, expires_at, created_at)
                VALUES ({Guid.CreateVersion7()}, {tenantId}, {memberId}, {runDate}, {HrBirthdayDeliveryChannels.Email}, NULL, {sentBy},
                        CAST({payload} AS jsonb), 'Pending', 0, {expiresAt}, {created})
                ON CONFLICT (tenant_id, member_id, run_date, channel) DO NOTHING
                """,
                CancellationToken.None);
        }
    }

    // camelCase, and never indented: this text is stored as jsonb and read back by the screen and by the delivery worker.
    private static readonly JsonSerializerOptions BirthdayJson = new(JsonSerializerDefaults.Web);

    private sealed record BirthdayPerson(Guid EmployeeId, string EmployeeName);

    private sealed record BirthdayPartsData(
        IReadOnlyList<BirthdayPerson> Today,
        int TodayTotal,
        IReadOnlyList<BirthdayPerson> Upcoming,
        int UpcomingTotal,
        string? UpcomingDate);

    // Only ids and names are copied: a birth date or an age in the payload (there should be none) never reaches a stored row.
    private static BirthdayPartsData BirthdayParts(JsonElement root) =>
        new(
            PeopleOf(root, "today"),
            root.GetProperty("todayTotal").GetInt32(),
            PeopleOf(root, "upcoming"),
            root.GetProperty("upcomingTotal").GetInt32(),
            root.TryGetProperty("upcomingDate", out var date) && date.ValueKind == JsonValueKind.String ? date.GetString() : null);

    private static List<BirthdayPerson> PeopleOf(JsonElement root, string name) =>
        [.. root.GetProperty(name).EnumerateArray().Select(person => new BirthdayPerson(
            person.GetProperty("employeeId").GetGuid(), person.GetProperty("employeeName").GetString() ?? string.Empty))];
}
