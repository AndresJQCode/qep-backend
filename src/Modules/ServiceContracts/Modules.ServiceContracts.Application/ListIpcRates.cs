using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

public sealed record ListIpcRatesQuery(Guid TenantId) : IQuery<IpcRateListDto>;

/// <summary>Las tasas del tenant, del año más reciente al más viejo, con su autor y si algún cambio pendiente las usa.</summary>
public sealed class ListIpcRatesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IServiceContractsMemberDirectory members,
    IIpcRateRepository ipcRates,
    IServicePriceScheduleRepository schedules)
    : IQueryHandler<ListIpcRatesQuery, IpcRateListDto>
{
    public async Task<IpcRateListDto> HandleAsync(ListIpcRatesQuery query, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ServiceContractsPermissions.Read, cancellationToken);

        var rates = await ipcRates.ListAsync(query.TenantId, cancellationToken);
        var inUse = (await schedules.ListPendingIpcYearsAsync(query.TenantId, cancellationToken)).ToHashSet();
        var authorIds = rates.Select(rate => rate.UpdatedByMemberId).OfType<Guid>().Distinct().ToArray();
        var authors = authorIds.Length == 0
            ? new Dictionary<Guid, ServiceContractsMemberRef>()
            : (await members.FindAsync(query.TenantId, authorIds, cancellationToken)).ToDictionary(member => member.MemberId);

        return new IpcRateListDto(rates
            .Select(rate => ServiceContractsMapping.ToIpcRateDto(
                rate,
                inUse.Contains(rate.Year),
                rate.UpdatedByMemberId is { } id
                    ? ServiceContractsMapping.ToMemberRefDto(id, authors.GetValueOrDefault(id))
                    : null))
            .ToList());
    }
}
