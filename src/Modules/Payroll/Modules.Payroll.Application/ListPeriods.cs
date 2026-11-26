using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Payroll.Application;

public sealed record ListPeriodsQuery(Guid TenantId, Guid CompanyId) : IQuery<PayrollPeriodListDto>;

/// <summary>
/// Los períodos de una empresa, del más nuevo al más viejo. No crea nada: quien sólo lee ve lo que ya se abrió y, sin períodos y con
/// hoy en una segunda quincena, <c>firstPeriodStartsOn</c>.
/// </summary>
public sealed class ListPeriodsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    PayrollCompanies companies,
    IPayrollPeriodRepository periods,
    PeriodActors actors,
    PeriodComposer composer)
    : IQueryHandler<ListPeriodsQuery, PayrollPeriodListDto>
{
    public async Task<PayrollPeriodListDto> HandleAsync(ListPeriodsQuery query, CancellationToken cancellationToken)
    {
        await PayrollAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, PayrollPermissions.PeriodRead);

        var company = await companies.RequireAsync(query.TenantId, query.CompanyId, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);
        var existing = await periods.ListAsync(query.TenantId, query.CompanyId, cancellationToken);

        return await ListComposer.ComposeAsync(composer, actors, query.TenantId, existing, company, calendar.Today, cancellationToken);
    }
}
