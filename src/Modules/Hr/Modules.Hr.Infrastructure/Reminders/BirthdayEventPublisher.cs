using System.Globalization;
using System.Text.Json;
using Modules.Hr.Application.BirthdayNotices;
using Modules.Hr.Infrastructure.Persistence;

namespace Modules.Hr.Infrastructure.Reminders;

/// <summary>
/// Spec 2026-11-26 §5.2: the birthday digest, written to the platform outbox for Notifications. The payload is camelCase (the
/// contract with the consumer, read by name) and carries names only, never a birth date or an age. It saves right away, like
/// <see cref="MedicalExamEventPublisher.PublishDueDigestAsync"/>, after the digest claim is won.
/// </summary>
internal sealed class BirthdayEventPublisher(HrDbContext dbContext)
{
    public const string DigestEvent = "hr.birthdays.digest.v1";

    // Web defaults: camelCase names. DateOnly writes as yyyy-MM-dd and DateTimeOffset as ISO 8601.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PublishDigestAsync(BirthdayDigestPayload payload, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var date = payload.RunDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        dbContext.Outbox.Add(new HrOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = DigestEvent,
            PayloadJson = JsonSerializer.Serialize(payload, Json),
            // The prefix keeps it apart from the medical digest of the same manager and day; 10 + 84 characters fit in varchar(100).
            CorrelationId = $"birthdays:{payload.TenantId:D}:{payload.MemberId:D}:{date}",
            OccurredAt = occurredAt,
            Attempts = 0,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
