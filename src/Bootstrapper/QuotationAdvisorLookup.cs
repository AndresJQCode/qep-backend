using Modules.Identity.Application;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// Resuelve correo y nombre de cada asesora de una cotización: <c>MemberId</c> → membresía
/// (Tenancy, que trae el nombre) → usuario (Identity, que trae el correo).
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
    public async Task<IReadOnlyDictionary<Guid, QuotationAdvisor>> FindAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> membershipIds,
        CancellationToken cancellationToken)
    {
        if (membershipIds.Count == 0)
        {
            return new Dictionary<Guid, QuotationAdvisor>();
        }

        // Sólo las membresías pedidas, en una consulta: el repositorio filtra por tenant e ids a la
        // vez, así que una de otro tenant no llega y no hace falta traer el tenant entero para
        // quedarse con una o dos asesoras.
        var ids = membershipIds.Distinct().Select(id => new MembershipId(id)).ToArray();
        var scoped = await memberships.ListByIdsAsync(
            new TenantId(tenantId), ids, cancellationToken);

        // El correo sí es una búsqueda por usuario: IUserDirectory sólo resuelve por id único,
        // igual que en ListMembershipsHandler. Acá el conteo es la cantidad de asesoras
        // **distintas** de la página —una o dos en la práctica—, no una por fila. El nombre y el
        // código de asesor salen de la membresía que ya se trajo, sin sumar consultas.
        var advisors = new Dictionary<Guid, QuotationAdvisor>(scoped.Count);
        foreach (var membership in scoped)
        {
            advisors[membership.Id.Value] = new QuotationAdvisor(
                await users.GetEmailAsync(membership.UserId, cancellationToken),
                membership.DisplayName,
                membership.AdvisorCode);
        }

        return advisors;
    }
}
