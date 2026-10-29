using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Billing.Application;

public sealed record GetCustomerProfileQuery(Guid TenantId, Guid CustomerId) : IQuery<ProfileDto>;

public sealed class GetCustomerProfileHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ICustomerBillingProfileRepository profiles,
    IBillingMemberDirectory members)
    : IQueryHandler<GetCustomerProfileQuery, ProfileDto>
{
    public async Task<ProfileDto> HandleAsync(GetCustomerProfileQuery query, CancellationToken cancellationToken)
    {
        await BillingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, BillingPermissions.Read, cancellationToken);

        // Acotado al tenant de la ruta: el perfil de un cliente de otro tenant no es «no tuyo», es «no existe».
        var profile = await profiles.FindAsync(query.TenantId, query.CustomerId, cancellationToken)
            ?? throw BillingNotFound.Profile(query.CustomerId);
        var found = await members.FindAsync(query.TenantId, [profile.AssigneeMemberId], cancellationToken);
        var assignee = found.Count > 0 ? found[0] : null;

        return BillingMapping.ToProfileDto(profile, BillingMapping.ToMemberRefDto(profile.AssigneeMemberId, assignee));
    }
}
