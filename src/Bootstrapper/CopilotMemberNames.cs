using Modules.Ai.Application;
using Modules.Identity.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-17 (copiloto) D-C12 y C-12: el nombre de quien usó el copiloto, por <b>usuario</b> (lo que guardan los hilos y
/// <c>usage_records.member_user_id</c>), con la regla de <see cref="CustomersAuthorNames"/>: <c>Membership.DisplayName</c> o, sin él, el
/// correo. Quien ya no tiene membresía ni usuario no viene en el diccionario.
/// </summary>
internal sealed class CopilotMemberNames(IMembershipRepository memberships, IUserDirectory users) : ICopilotMemberNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> FindAsync(
        Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        var names = new Dictionary<Guid, string>();
        // Pocos miembros por tenant usan el copiloto en un mes: una lectura de membresía por miembro, como las notas de clientes.
        var found = new List<(Guid UserId, Membership? Membership)>();
        foreach (var userId in userIds.Distinct())
        {
            found.Add((userId, await memberships.FindByUserAndTenantAsync(userId, new TenantId(tenantId), cancellationToken)));
        }

        // Los correos de quienes no tienen nombre, en cambio, van en un solo viaje a Identity.
        var emails = await users.GetEmailsAsync(
            found.Where(item => string.IsNullOrWhiteSpace(item.Membership?.DisplayName))
                .Select(item => item.UserId)
                .ToList(),
            cancellationToken);
        foreach (var (userId, membership) in found)
        {
            var name = !string.IsNullOrWhiteSpace(membership?.DisplayName)
                ? membership.DisplayName
                : emails.GetValueOrDefault(userId);
            if (!string.IsNullOrWhiteSpace(name))
            {
                names[userId] = name;
            }
        }

        return names;
    }
}
