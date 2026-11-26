using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record GetPeriodEmployeeQuery(Guid TenantId, Guid PeriodId, Guid EmployeeId) : IQuery<PayrollEmployeeDetailDto>;

/// <summary>
/// Un empleado en la quincena (R-18). Un excluido responde 200 con <c>result: null</c> y las listas vacías en Borrador, Liquidado y
/// Reabierto (I-13); 404 <c>payroll.employee.not_in_period</c> sólo si no tiene contrato de la empresa en la quincena.
/// </summary>
public sealed class GetPeriodEmployeeHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPayrollPeriodRepository periods,
    PeriodActors actors,
    PeriodComposer composer)
    : IQueryHandler<GetPeriodEmployeeQuery, PayrollEmployeeDetailDto>
{
    public async Task<PayrollEmployeeDetailDto> HandleAsync(GetPeriodEmployeeQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, PayrollPermissions.PeriodRead);

        var period = await periods.FindAsync(query.TenantId, new PayrollPeriodId(query.PeriodId), cancellationToken)
            ?? throw PayrollNotFound.Period(query.PeriodId);

        return await composer.ComposeEmployeeAsync(
            period, query.EmployeeId, await actors.CurrentAsync(query.TenantId, cancellationToken), actors.CallerPermissions(), cancellationToken);
    }
}
