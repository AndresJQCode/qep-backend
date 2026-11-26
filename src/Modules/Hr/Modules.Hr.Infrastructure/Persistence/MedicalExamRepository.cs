using Microsoft.EntityFrameworkCore;
using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class MedicalExamRepository(HrDbContext dbContext) : IMedicalExamRepository
{
    public async Task<IReadOnlyList<MedicalExam>> ListExamsAsync(
        Guid tenantId, EmployeeId employeeId, CancellationToken cancellationToken) =>
        await dbContext.MedicalExams
            .Where(exam => exam.TenantId == tenantId && exam.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ExitExamRequirement>> ListRequirementsAsync(
        Guid tenantId, EmployeeId employeeId, CancellationToken cancellationToken) =>
        await dbContext.ExitExamRequirements
            .Where(requirement => requirement.TenantId == tenantId && requirement.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<MedicalExamEmployeeRef>> FindEmployeesByDocumentAsync(
        Guid tenantId, IReadOnlyCollection<string> documentNumbers, CancellationToken cancellationToken)
    {
        var numbers = documentNumbers.ToArray();
        var rows = await dbContext.Employees
            .AsNoTracking()
            .Where(employee => employee.TenantId == tenantId && numbers.Contains(employee.DocumentNumber))
            .Select(employee => new { employee.Id, employee.DocumentType, employee.DocumentNumber, employee.FirstName, employee.LastName })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new MedicalExamEmployeeRef(row.Id, row.DocumentType, row.DocumentNumber, $"{row.FirstName} {row.LastName}"))
            .ToList();
    }

    public async Task<IReadOnlySet<(EmployeeId EmployeeId, MedicalExamType Type, DateOnly ExamDate)>> ListExamKeysAsync(
        Guid tenantId, IReadOnlyCollection<EmployeeId> employeeIds, CancellationToken cancellationToken)
    {
        var ids = employeeIds.ToArray();
        var rows = await dbContext.MedicalExams
            .AsNoTracking()
            .Where(exam => exam.TenantId == tenantId && ids.Contains(exam.EmployeeId))
            .Select(exam => new { exam.EmployeeId, exam.Type, exam.ExamDate })
            .ToListAsync(cancellationToken);

        return rows.Select(row => (row.EmployeeId, row.Type, row.ExamDate)).ToHashSet();
    }

    public void Add(MedicalExam exam) => dbContext.MedicalExams.Add(exam);

    public void Remove(MedicalExam exam) => dbContext.MedicalExams.Remove(exam);

    public void Add(ExitExamRequirement requirement) => dbContext.ExitExamRequirements.Add(requirement);
}
