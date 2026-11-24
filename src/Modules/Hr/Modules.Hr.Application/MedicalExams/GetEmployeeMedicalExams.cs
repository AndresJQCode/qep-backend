using BuildingBlocks.Application;

namespace Modules.Hr.Application.MedicalExams;

public sealed record GetEmployeeMedicalExamsQuery(Guid TenantId, Guid EmployeeId) : IQuery<EmployeeMedicalExamsDto>;

/// <summary>
/// The exams of an employee with their control status (spec 2026-11-23 §7). It also answers for an employee outside the control
/// universe, with <c>status: null</c> and the whole history.
/// </summary>
public sealed class GetEmployeeMedicalExamsHandler(MedicalExamWrites writes)
    : IQueryHandler<GetEmployeeMedicalExamsQuery, EmployeeMedicalExamsDto>
{
    public async Task<EmployeeMedicalExamsDto> HandleAsync(GetEmployeeMedicalExamsQuery query, CancellationToken cancellationToken)
    {
        await writes.AuthorizeReadAsync(query.TenantId, cancellationToken);
        var context = await writes.LoadAsync(query.TenantId, query.EmployeeId, cancellationToken);

        return await writes.ComposeAsync(query.TenantId, context, cancellationToken);
    }
}
