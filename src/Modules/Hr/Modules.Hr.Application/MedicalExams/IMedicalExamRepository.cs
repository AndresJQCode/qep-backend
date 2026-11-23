using Modules.Hr.Domain;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// Medical exams and exit requirements (spec 2026-11-23). Every method carries the tenant: a read never crosses tenants. What it
/// returns is tracked, so whatever the use case does is saved with the unit of work of the same request.
/// </summary>
public interface IMedicalExamRepository
{
    void Add(ExitExamRequirement requirement);
}
