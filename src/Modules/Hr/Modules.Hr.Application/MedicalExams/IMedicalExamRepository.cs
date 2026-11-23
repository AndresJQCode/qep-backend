using Modules.Hr.Domain;

namespace Modules.Hr.Application.MedicalExams;

public sealed record MedicalExamEmployeeRef(EmployeeId Id, HrDocumentType DocumentType, string DocumentNumber, string FullName);

/// <summary>
/// Medical exams and exit requirements (spec 2026-11-23). Every method carries the tenant: a read never crosses tenants. What it
/// returns is tracked, so whatever the use case does is saved with the unit of work of the same request.
/// </summary>
public interface IMedicalExamRepository
{
    /// <summary>All the exams of the employee, tracked, in no particular order.</summary>
    Task<IReadOnlyList<MedicalExam>> ListExamsAsync(Guid tenantId, EmployeeId employeeId, CancellationToken cancellationToken);

    /// <summary>All the exit requirements of the employee, tracked, in no particular order.</summary>
    Task<IReadOnlyList<ExitExamRequirement>> ListRequirementsAsync(
        Guid tenantId, EmployeeId employeeId, CancellationToken cancellationToken);

    /// <summary>
    /// The employees of the tenant, in any status, whose document number is one of <paramref name="documentNumbers"/>: the import
    /// crosses its rows with them. Read-only.
    /// </summary>
    Task<IReadOnlyList<MedicalExamEmployeeRef>> FindEmployeesByDocumentAsync(
        Guid tenantId, IReadOnlyCollection<string> documentNumbers, CancellationToken cancellationToken);

    /// <summary>The type and date of every exam already recorded for these employees: the import's duplicates. Read-only.</summary>
    Task<IReadOnlySet<(EmployeeId EmployeeId, MedicalExamType Type, DateOnly ExamDate)>> ListExamKeysAsync(
        Guid tenantId, IReadOnlyCollection<EmployeeId> employeeIds, CancellationToken cancellationToken);

    void Add(MedicalExam exam);

    void Remove(MedicalExam exam);

    void Add(ExitExamRequirement requirement);
}
