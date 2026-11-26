using System.Globalization;
using System.Text.Json;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Infrastructure.Persistence;

namespace Modules.Hr.Infrastructure.Reminders;

/// <summary>
/// Spec 2026-11-23 §9.2: the two events of the medical exams, written to the platform outbox for the bell of Notifications. The payloads
/// are camelCase (the contract with the consumer, read by name) and carry only ids, a name, a date and a count: never restrictions,
/// recommendations, the provider or the type of an exam. <see cref="AddExitRequired"/> does not save (it travels with the termination);
/// <see cref="PublishDueDigestAsync"/> saves right away, like <c>OverdueDigestEventPublisher</c>, after the digest claim is won.
/// </summary>
internal sealed class MedicalExamEventPublisher(HrDbContext dbContext) : IMedicalExamEventPublisher
{
    public const string DueDigestEvent = "hr.medical-exams.due-digest.v1";

    public const string ExitRequiredEvent = "hr.medical-exams.exit-required.v1";

    public void AddExitRequired(
        Guid tenantId,
        Guid memberId,
        Guid employeeId,
        string employeeName,
        DateOnly dueDate,
        Guid contractId,
        DateTimeOffset occurredAt) =>
        dbContext.Outbox.Add(new HrOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = ExitRequiredEvent,
            PayloadJson = JsonSerializer.Serialize(
                new ExitRequiredPayload(tenantId, memberId, employeeId, employeeName, Format(dueDate), occurredAt)),
            // Three guids in the :D form are 110 characters and correlation_id is varchar(100): the compact form (32 hex) fits in 98.
            CorrelationId = $"{tenantId:N}:{memberId:N}:{contractId:N}",
            OccurredAt = occurredAt,
            Attempts = 0,
        });

    public async Task PublishDueDigestAsync(
        Guid tenantId, Guid memberId, DateOnly runDate, int pendingCount, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var date = Format(runDate);
        dbContext.Outbox.Add(new HrOutboxMessage
        {
            Id = Guid.CreateVersion7(),
            EventName = DueDigestEvent,
            PayloadJson = JsonSerializer.Serialize(new DigestPayload(tenantId, memberId, date, pendingCount, occurredAt)),
            CorrelationId = $"{tenantId:D}:{memberId:D}:{date}",
            OccurredAt = occurredAt,
            Attempts = 0,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // camelCase names are the contract with Notifications; System.Text.Json writes a record's parameter names as they are.
    private sealed record DigestPayload(Guid tenantId, Guid memberId, string runDate, int pendingCount, DateTimeOffset occurredAt);

    private sealed record ExitRequiredPayload(
        Guid tenantId, Guid memberId, Guid employeeId, string employeeName, string dueDate, DateTimeOffset occurredAt);
}
