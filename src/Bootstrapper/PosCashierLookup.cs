using Modules.Identity.Application;
using Modules.Pos.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>DisplayName ?? Email, de las mismas fuentes que QuotationAdvisorLookup.</summary>
internal sealed class PosCashierLookup(IMembershipRepository memberships, IUserDirectory users) : IPosCashierLookup
{
    private const string FallbackName = "Cajero";

    public async Task<string?> FindNameAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken)
    {
        var scoped = await memberships.ListByIdsAsync(
            new TenantId(tenantId), [new MembershipId(membershipId)], cancellationToken);
        var membership = scoped.SingleOrDefault();
        if (membership is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(membership.DisplayName))
        {
            return membership.DisplayName;
        }

        // Una membresía activa nunca devuelve null: el handler congelaría el GUID como nombre del
        // cajero en la caja y en el ticket. Sin nombre ni correo, una etiqueta legible.
        var email = await users.GetEmailAsync(membership.UserId, cancellationToken);
        return string.IsNullOrWhiteSpace(email) ? FallbackName : email;
    }
}
