using System.Globalization;
using System.Text.Json;
using Modules.ServiceContracts.Infrastructure.Persistence;

namespace Modules.ServiceContracts.Infrastructure.Reminders;

/// <summary>
/// Spec 2026-11-22 §6, §12, D-17 and D-18: «tienes N cobros vencidos» for the bell of a member who receives the daily digest. The event is
/// written and saved right after <see cref="DailyDigestStore.TryClaimAsync"/> wins and before the email is looked at, so a member without
/// an email still sees the bell. If the process dies between the claim and this save, the digest row stays <c>Sending</c> and its lease
/// (five minutes) lets the next run of the same day claim it again and publish; if it was already published, the consumer's
/// <c>dedupe_key</c> for the day absorbs the second one. The outbox row carries the time of the write; the payload, the run's.
/// </summary>
internal sealed class OverdueDigestEventPublisher(ServiceContractsDbContext dbContext)
{
    public const string EventName = "billing.charges.overdue-digest.v1";

    public async Task PublishAsync(
        Guid tenantId, Guid memberId, DateOnly runDate, int overdueCount, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var date = runDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        dbContext.Outbox.Add(new ServiceContractsOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = EventName,
            PayloadJson = JsonSerializer.Serialize(new DigestPayload(tenantId, memberId, date, overdueCount, occurredAt)),
            CorrelationId = $"{tenantId:D}:{memberId:D}:{date}",
            OccurredAt = occurredAt,
            Attempts = 0,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // camelCase names are the contract with Notifications (read by name); System.Text.Json writes a record's parameter names as they are.
    private sealed record DigestPayload(Guid tenantId, Guid memberId, string runDate, int overdueCount, DateTimeOffset occurredAt);
}
