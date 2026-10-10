using Modules.Identity.Application;
using Modules.Messaging.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// <c>sentBy.displayName</c> (spec 2026-10-09 §8.7): copia de <see cref="IntegrationsConnectionAuthorNames"/>,
/// <c>Membership.DisplayName ?? correo</c>. Sólo membresías del tenant; la que ya no está no aparece en el
/// diccionario, y Messaging la muestra como «Miembro eliminado» (D-M20): la pantalla nunca recibe <c>null</c>.
/// </summary>
internal sealed class MessagingMemberNames(IMembershipRepository memberships, IUserDirectory users)
    : IMessagingMemberNames
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
