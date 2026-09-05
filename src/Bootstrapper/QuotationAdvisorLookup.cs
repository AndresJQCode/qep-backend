using Modules.Identity.Application;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Resuelve el correo de cada asesora para el listado de cotizaciones: <c>MemberId</c> →
/// membresía (Tenancy) → usuario (Identity) → correo.
///
/// Vive acá y no en ninguno de los tres módulos, mismo criterio que
/// <see cref="QuotationCustomerLookup"/>: el composition root es el único lugar donde ese
/// acoplamiento es legítimo.
/// </summary>
internal sealed class QuotationAdvisorLookup(
    IMembershipRepository memberships,
    IUserDirectory users)
    : IQuotationAdvisorLookup
{
    public async Task<IReadOnlyDictionary<Guid, string?>> FindEmailsAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        if (membershipIds.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        // Las membresías del tenant se traen de una: son pocas por tenant (mismo supuesto que
        // documenta ListMembershipsHandler) y así el filtro por id no cuesta una consulta por
        // asesora.
        var wanted = membershipIds.ToHashSet();
        var scoped = (await memberships.ListByTenantAsync(
                new TenantId(tenantId), cancellationToken))
            .Where(membership => wanted.Contains(membership.Id.Value))
            .ToList();

        // El correo sí es una búsqueda por usuario: IUserDirectory sólo resuelve por id único,
        // igual que en ListMembershipsHandler. Acá el conteo es la cantidad de asesoras
        // **distintas** de la página —una o dos en la práctica—, no una por fila.
        var emails = new Dictionary<Guid, string?>(scoped.Count);
        foreach (var membership in scoped)
        {
            emails[membership.Id.Value] =
                await users.GetEmailAsync(membership.UserId, cancellationToken);
        }

        return emails;
    }
}
