using Modules.Identity.Application;
using Modules.Shopify.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>Spec 2026-10-15 §6.1 y §6.2: el asesor de la tienda, por Tenancy e Identity. Copia la regla de
/// <see cref="PipelineActiveMembers"/> (estado filtrado acá) y la del nombre de <see cref="MessagingMemberNames"/>
/// (<c>DisplayName ?? correo</c>).</summary>
internal sealed class ShopifyMemberDirectory(IMembershipRepository memberships, IUserDirectory users) : IShopifyMemberDirectory
{
    public async Task<bool> IsActiveMemberAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        await FindAsync(tenantId, membershipId, cancellationToken) is { State: MembershipState.Active };

    public async Task<string?> NameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        var membership = await FindAsync(tenantId, membershipId, cancellationToken);
        if (membership is null)
        {
            return null;
        }

        var name = !string.IsNullOrWhiteSpace(membership.DisplayName)
            ? membership.DisplayName
            : await users.GetEmailAsync(membership.UserId, cancellationToken);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    // ListByIdsAsync ya filtra por tenant: una membresía de otro tenant no aparece.
    private async Task<Membership?> FindAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        var found = await memberships.ListByIdsAsync(new TenantId(tenantId), [new MembershipId(membershipId)], cancellationToken);
        return found.Count > 0 ? found[0] : null;
    }
}
