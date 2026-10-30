using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.ServiceContracts.Application;

public sealed record GetCustomerProfileQuery(Guid TenantId, Guid CustomerId) : IQuery<ProfileDto>;

public sealed class GetCustomerProfileHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICustomerBillingProfileRepository profiles,
    IServiceContractsMemberDirectory members)
    : IQueryHandler<GetCustomerProfileQuery, ProfileDto>
{
    public async Task<ProfileDto> HandleAsync(GetCustomerProfileQuery query, CancellationToken cancellationToken)
    {
        await ServiceContractsAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ServiceContractsPermissions.Read, cancellationToken);

        // Acotado al tenant de la ruta: el perfil de un cliente de otro tenant no es «no tuyo», es «no existe».
        var profile = await profiles.FindAsync(query.TenantId, query.CustomerId, cancellationToken)
            ?? throw ServiceContractsNotFound.Profile(query.CustomerId);
        var found = await members.FindAsync(query.TenantId, [profile.AssigneeMemberId], cancellationToken);
        var assignee = found.Count > 0 ? found[0] : null;

        return ServiceContractsMapping.ToProfileDto(profile, ServiceContractsMapping.ToMemberRefDto(profile.AssigneeMemberId, assignee));
    }
}
