using Modules.Identity.Application;
using Modules.Siigo.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="ISiigoMemberDirectory"/> (spec 2026-11-20, vendedores vinculados): membresías de Tenancy y correos de
/// Identity, como <see cref="QuotationAdvisorLookup"/>. El composition root es el único lugar donde ese acoplamiento es legítimo.
/// </summary>
internal sealed class SiigoMemberDirectory(IMembershipRepository memberships, IUserDirectory users) : ISiigoMemberDirectory
{
    public async Task<IReadOnlyList<SiigoMemberRef>> ListAsync(
        Guid tenantId, IReadOnlyCollection<Guid> alsoInclude, CancellationToken cancellationToken)
    {
        // Las del tenant: el repositorio ya filtra por tenant, así que un id de otro en alsoInclude no aparece.
        var all = await memberships.ListByTenantAsync(new TenantId(tenantId), cancellationToken);
        var wanted = alsoInclude.ToHashSet();
        var listed = all
            .Where(membership => membership.State == MembershipState.Active || wanted.Contains(membership.Id.Value))
            .ToList();

        // Los correos en un solo viaje a Identity.
        var emails = await users.GetEmailsAsync(listed.Select(membership => membership.UserId).ToList(), cancellationToken);
        return
        [
            .. listed.Select(membership => new SiigoMemberRef(
                membership.Id.Value,
                membership.DisplayName,
                emails.GetValueOrDefault(membership.UserId),
                membership.State == MembershipState.Active)),
        ];
    }
}
