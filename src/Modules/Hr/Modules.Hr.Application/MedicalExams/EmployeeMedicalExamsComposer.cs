using Modules.Hr.Domain;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// Builds <see cref="EmployeeMedicalExamsDto"/> (spec 2026-11-23 §5.4 and §7.1) from what the use case already loaded. Shared by the
/// read and by every write, so the screen always repaints from the same shape and nothing is derived in the browser.
/// </summary>
public sealed class EmployeeMedicalExamsComposer(IHrSettingsRepository settings, IHrBusinessCalendar businessCalendar)
{
    public async Task<EmployeeMedicalExamsDto> ComposeAsync(
        Guid tenantId,
        Employee employee,
        IReadOnlyList<MedicalExam> exams,
        IReadOnlyList<ExitExamRequirement> requirements,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var ordered = exams
            .OrderByDescending(exam => exam.ExamDate)
            .ThenByDescending(exam => exam.CreatedAt)
            .ThenByDescending(exam => exam.Id.Value)
            .ToList();

        var frequency = await FrequencyOfAsync(tenantId, employee, today, cancellationToken);
        var next = frequency is { } months && ordered.Count > 0
            ? MedicalExamRules.NextPeriodicDue(ordered[0].ExamDate, months)
            : (DateOnly?)null;
        var status = MedicalExamRules.StatusOf(
            employee.Status,
            requirements.Any(requirement => requirement.Status == ExitExamStatus.Pending),
            ordered.Count > 0,
            next,
            today);

        var latest = requirements
            .OrderByDescending(requirement => requirement.TerminatedOn)
            .ThenByDescending(requirement => requirement.CreatedAt)
            .FirstOrDefault();

        return new EmployeeMedicalExamsDto(
            status?.ToString(),
            status is null ? null : next,
            frequency,
            latest is null ? null : await ToDtoAsync(tenantId, latest, today, cancellationToken),
            ordered.Select(ToDto).ToList());
    }

    /// <summary>The frequency of the position of the assignment in force on the active contract; none without an active contract.</summary>
    private async Task<int?> FrequencyOfAsync(Guid tenantId, Employee employee, DateOnly today, CancellationToken cancellationToken)
    {
        if (employee.ActiveContract is not { Assignments.Count: > 0 } contract)
        {
            return null;
        }

        var positionId = EffectiveDated.CurrentOf(contract.Assignments, today).PositionId;
        return (await settings.FindPositionAsync(tenantId, positionId, cancellationToken))?.ExamFrequencyMonths;
    }

    private async Task<ExitRequirementDto> ToDtoAsync(
        Guid tenantId, ExitExamRequirement requirement, DateOnly today, CancellationToken cancellationToken)
    {
        var pending = requirement.Status == ExitExamStatus.Pending;
        var overdue = pending && today > requirement.DueDate;
        // Resolved once for the request: the composer asks for a single requirement (the most recent one), and the counter is only built
        // when it is pending and not yet overdue.
        int? left = pending
            ? overdue ? 0 : (await businessCalendar.CounterAsync(tenantId, cancellationToken)).Count(today, requirement.DueDate)
            : null;

        return new ExitRequirementDto(
            requirement.ContractId.Value,
            requirement.TerminatedOn,
            requirement.DueDate,
            requirement.Status.ToString(),
            left,
            overdue,
            requirement.NotifiedOn,
            requirement.Notes,
            requirement.ExamId?.Value);
    }

    private static MedicalExamDto ToDto(MedicalExam exam) => new(
        exam.Id.Value,
        exam.Type.ToString(),
        exam.ExamDate,
        exam.Provider,
        exam.Restrictions,
        exam.Recommendations,
        exam.RestrictionsUntil,
        exam.DocumentFileId is not null,
        exam.Version);
}
