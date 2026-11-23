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

    public void Add(MedicalExam exam) => dbContext.MedicalExams.Add(exam);

    public void Remove(MedicalExam exam) => dbContext.MedicalExams.Remove(exam);

    public void Add(ExitExamRequirement requirement) => dbContext.ExitExamRequirements.Add(requirement);
}
