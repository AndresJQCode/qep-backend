using Modules.Identity.Application;
using Modules.Integrations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// <c>createdBy.displayName</c> (spec 2026-10-08, P23): <c>Membership.DisplayName ?? correo</c>, de las
/// mismas fuentes que <see cref="PosCashierLookup"/>. Sólo membresías del tenant; la que ya no está no
/// aparece en el diccionario y la pantalla muestra <c>null</c>.
/// </summary>
internal sealed class IntegrationsConnectionAuthorNames(IMembershipRepository memberships, IUserDirectory users)
    : IConnectionAuthorNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        if (memberIds.Count == 0)
        {
            return names;
        }

        var scoped = await memberships.ListByIdsAsync(
            new TenantId(tenantId), memberIds.Select(id => new MembershipId(id)).ToArray(), cancellationToken);
        foreach (var membership in scoped)
        {
            var name = !string.IsNullOrWhiteSpace(membership.DisplayName)
                ? membership.DisplayName
                : await users.GetEmailAsync(membership.UserId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[membership.Id.Value] = name;
            }
        }

        return names;
    }
}
