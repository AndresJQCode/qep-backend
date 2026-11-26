using FluentValidation;
using Modules.Hr.Domain;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// <c>POST</c> and <c>PUT</c> of an exam (spec 2026-11-23 §7.1). The <c>PUT</c> replaces everything and may change the type and the
/// date. Everything is nullable so that the validator answers with the field and not with a binding 400.
/// </summary>
public sealed record MedicalExamRequest(
    string? Type,
    DateOnly? ExamDate,
    string? Provider,
    string? Restrictions,
    string? Recommendations,
    DateOnly? RestrictionsUntil);

/// <param name="NotifiedOn">The day the employer told the worker about the exit exam. Never in the future.</param>
public sealed record NotAttendedRequest(DateOnly? NotifiedOn, string? Notes);

/// <summary>
/// One exam of the employee. <c>Restrictions</c>, <c>Recommendations</c> and <c>Provider</c> are health data: only a caller with
/// <c>hr.medical.read</c> ever receives this DTO.
/// </summary>
public sealed record MedicalExamDto(
    Guid Id,
    string Type,
    DateOnly ExamDate,
    string? Provider,
    string? Restrictions,
    string? Recommendations,
    DateOnly? RestrictionsUntil,
    bool HasDocument,
    long Version);

/// <param name="BusinessDaysLeft">
/// Only while <c>Pending</c>: the business days from today to the deadline, 0 once it is due today or overdue. Counted by the
/// backend with the tenant calendar (holidays included); <c>null</c> otherwise.
/// </param>
/// <param name="Overdue">Only while <c>Pending</c>: today is after <c>DueDate</c>.</param>
public sealed record ExitRequirementDto(
    Guid ContractId,
    DateOnly TerminatedOn,
    DateOnly DueDate,
    string Status,
    int? BusinessDaysLeft,
    bool Overdue,
    DateOnly? NotifiedOn,
    string? Notes,
    Guid? ExamId);

/// <summary>
/// What every read and write of an employee's exams answers (spec 2026-11-23 §7.1): the screen repaints from it and never derives a
/// status. <c>Status</c> is <c>null</c> outside the control universe (a deactivated employee, or a retired one whose exit exam is
/// closed): the history is still there. <c>ExitRequirement</c> is the most recent one, <c>null</c> if there never was.
/// </summary>
public sealed record EmployeeMedicalExamsDto(
    string? Status,
    DateOnly? NextPeriodicDue,
    int? ExamFrequencyMonths,
    ExitRequirementDto? ExitRequirement,
    IReadOnlyList<MedicalExamDto> Exams);

// The validators are public on purpose: AddValidatorsFromAssembly only registers public types, and one that is not registered lets
// the body through unvalidated.
public sealed class MedicalExamRequestValidator : HrValidator<MedicalExamRequest>
{
    public MedicalExamRequestValidator()
    {
        RuleFor(request => request.Type)
            .MustBeAnEnum<MedicalExamRequest, MedicalExamType>("Elige un tipo de examen válido.");
        RuleFor(request => request.ExamDate).NotNull().WithMessage("Escribe la fecha del examen.");
        RuleFor(request => request.Provider).MaxTrimmed(200, "La IPS o el médico");
        RuleFor(request => request.Restrictions).MaxTrimmed(2000, "Las restricciones");
        RuleFor(request => request.Recommendations).MaxTrimmed(2000, "Las recomendaciones");
    }
}

public sealed class NotAttendedRequestValidator : HrValidator<NotAttendedRequest>
{
    public NotAttendedRequestValidator()
    {
        RuleFor(request => request.NotifiedOn).NotNull().WithMessage("Escribe la fecha en que se avisó al trabajador.");
        RuleFor(request => request.Notes).MaxTrimmed(500, "Las notas");
    }
}
