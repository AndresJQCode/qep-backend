namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// The daily run of one tenant (spec 2026-11-23 §9.1): from the local hour of the tenant, only if it is active and has the module
/// <c>hr</c>, once per tenant and day, and one digest per manager and day. The worker only decides which tenants to ask.
/// </summary>
public interface IHrMedicalExamsReminderProcessor
{
    Task ProcessTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Writes the integration events of the medical exams to the platform outbox (spec 2026-11-23 §9.2). Only
/// <see cref="AddExitRequired"/> is here: it is called by <c>TerminateContract</c> and does <b>not</b> save, so the event travels in
/// the transaction of the termination itself. The payloads carry ids, a name, a date and a count: never restrictions,
/// recommendations, the provider or the type of an exam.
/// </summary>
public interface IMedicalExamEventPublisher
{
    void AddExitRequired(
        Guid tenantId,
        Guid memberId,
        Guid employeeId,
        string employeeName,
        DateOnly dueDate,
        Guid contractId,
        DateTimeOffset occurredAt);
}
