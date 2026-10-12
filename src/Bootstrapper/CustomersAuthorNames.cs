using Modules.Customers.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// <c>author.displayName</c> de las notas de un cliente (spec 2026-10-11 §4.2): <c>Membership.DisplayName ?? correo</c>,
/// de las mismas fuentes que <see cref="MessagingMemberNames"/> y <see cref="PosCashierLookup"/>. A diferencia de
/// aquellas, la nota guarda el <b>usuario</b> y no la membresía, así que se busca la membresía del usuario en el
/// tenant. Quien ya no tiene membresía ni usuario no aparece en el diccionario, y Customers lo muestra como «Usuario
/// eliminado»: la pantalla nunca recibe <c>null</c>.
/// </summary>
internal sealed class CustomersAuthorNames(IMembershipRepository memberships, IUserDirectory users) : ICustomerAuthorNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var names = new Dictionary<Guid, string>();
        // Pocos autores distintos por ficha: una lectura por autor es más simple que un método de lote en Tenancy.
        foreach (var userId in userIds.Distinct())
        {
            var membership = await memberships.FindByUserAndTenantAsync(userId, new TenantId(tenantId), cancellationToken);
            var name = !string.IsNullOrWhiteSpace(membership?.DisplayName)
                ? membership.DisplayName
                : await users.GetEmailAsync(userId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[userId] = name;
            }
        }

        return names;
    }
}
