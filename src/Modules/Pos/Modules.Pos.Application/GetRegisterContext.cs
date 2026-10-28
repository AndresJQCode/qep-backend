using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Pos.Application;

/// <summary>Todo lo que la pantalla de caja necesita para decidir qué dibujar, en un viaje (endpoint 1).</summary>
public sealed record GetRegisterContextQuery(Guid TenantId) : IQuery<RegisterContextResponse>;

public sealed class GetRegisterContextHandler(
    ICashSessionRepository sessions,
    IPosCompanyLookup companies,
    IPosCashierLookup cashiers,
    IMembershipDirectory membershipDirectory,
    IExecutionContext executionContext,
    ITenantClock tenantClock,
    ITenantDefaultCurrency tenantDefaultCurrency)
    : IQueryHandler<GetRegisterContextQuery, RegisterContextResponse>
{
    public async Task<RegisterContextResponse> HandleAsync(
        GetRegisterContextQuery query, CancellationToken cancellationToken)
    {
        PosAuthorization.EnsureAuthorized(executionContext, query.TenantId, PosPermissions.RegisterOperate);
        var cashier = await PosCashierResolver.ResolveAsync(membershipDirectory, executionContext, query.TenantId, cancellationToken);
        var name = await cashiers.FindNameAsync(query.TenantId, cashier.Value, cancellationToken) ?? cashier.Value.ToString();
        var session = await sessions.FindOpenByCashierAsync(query.TenantId, cashier, cancellationToken);
        var active = await companies.ListActiveAsync(query.TenantId, cancellationToken);
        var calendar = await tenantClock.GetAsync(query.TenantId, cancellationToken);
        var defaultCurrency = await tenantDefaultCurrency.GetAsync(query.TenantId, cancellationToken);

        return new RegisterContextResponse(
            new PosCashierResponse(cashier.Value, name),
            session is null ? null : PosSessionMapping.ToOpenResponse(session, calendar),
            active.Select(company => new PosCompanyOption(company.Id, company.Name, company.TaxId)).ToArray(),
            active.Count == 1 ? active[0].Id : null,
            defaultCurrency);
    }
}
