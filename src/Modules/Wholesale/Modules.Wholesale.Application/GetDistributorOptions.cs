using BuildingBlocks.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Wholesale.Application;

public sealed record GetDistributorOptionsQuery(Guid TenantId) : IQuery<DistributorOptionsDto>;

/// <summary>
/// Lo que el formulario de invitar o editar puede elegir: listas, asesores activos (D-15) y empresas activas con su
/// elegibilidad (D-2), todo por nombre, y los números de WhatsApp listos para el aviso (spec 2026-11-18). Exige
/// <c>wholesale.distributor.manage</c>: sólo sirve para escribir.
/// </summary>
public sealed class GetDistributorOptionsHandler(
    IExecutionContext executionContext,
    IWholesalePriceLists priceLists,
    IWholesaleMemberDirectory members,
    IWholesaleCompanyLookup companies,
    IWholesaleWhatsAppConnections whatsAppConnections,
    ITenantDirectory tenants)
    : IQueryHandler<GetDistributorOptionsQuery, DistributorOptionsDto>
{
    public async Task<DistributorOptionsDto> HandleAsync(GetDistributorOptionsQuery query, CancellationToken cancellationToken)
    {
        WholesaleAuthorization.EnsureAuthorized(executionContext, query.TenantId, WholesalePermissions.DistributorManage);
        var lists = await priceLists.ListAsync(query.TenantId, cancellationToken);
        var advisors = await members.ListActiveAsync(query.TenantId, cancellationToken);
        var active = await companies.ListActiveAsync(query.TenantId, cancellationToken);
        var ready = await whatsAppConnections.ListReadyAsync(query.TenantId, cancellationToken);
        var settings = await tenants.GetRegionalSettingsAsync(new TenantId(query.TenantId), cancellationToken);

        return new DistributorOptionsDto(
            lists
                .OrderBy(list => list.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(list => new DistributorPriceListDto(list.Id.Value, list.Name, list.AdjustmentPercent))
                .ToList(),
            advisors
                .OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(member => new DistributorAdvisorDto(member.MemberId, member.Name))
                .ToList(),
            active
                .OrderBy(company => company.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(company => new DistributorCompanyOptionDto(
                    company.Id, company.Name, company.DocumentType, company.DocumentNumber, company.Eligible))
                .ToList(),
            ready.Select(connection => new DistributorWhatsAppConnectionDto(connection.Id, connection.Label)).ToList(),
            settings?.Country ?? DistributorDefaults.Country);
    }
}
