using Modules.Hr.Application.MedicalExams;
using Modules.Hr.Domain;

namespace Modules.Hr.Infrastructure.Persistence;

internal sealed class MedicalExamRepository(HrDbContext dbContext) : IMedicalExamRepository
{
    public void Add(ExitExamRequirement requirement) => dbContext.ExitExamRequirements.Add(requirement);
}
