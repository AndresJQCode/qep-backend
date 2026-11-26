using BuildingBlocks.Application;
using Modules.Payroll.Domain;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record GetPeriodQuery(Guid TenantId, Guid PeriodId) : IQuery<PayrollPeriodDetailDto>;

/// <summary>
/// El detalle de un período. El id de un período de otro tenant pedido con la ruta del propio responde el mismo 404 que un id
/// inventado.
/// </summary>
public sealed class GetPeriodHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IPayrollPeriodRepository periods,
    PeriodActors actors,
    PeriodComposer composer)
    : IQueryHandler<GetPeriodQuery, PayrollPeriodDetailDto>
{
    public async Task<PayrollPeriodDetailDto> HandleAsync(GetPeriodQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, PayrollPermissions.PeriodRead);

        var period = await periods.FindAsync(query.TenantId, new PayrollPeriodId(query.PeriodId), cancellationToken)
            ?? throw PayrollNotFound.Period(query.PeriodId);

        return await composer.ComposeAsync(
            period, await actors.CurrentAsync(query.TenantId, cancellationToken), actors.CallerPermissions(), cancellationToken);
    }
}
