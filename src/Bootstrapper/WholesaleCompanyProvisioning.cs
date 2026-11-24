using Modules.Companies.Application;
using Modules.Wholesale.Application;

namespace Bootstrapper;

/// <summary>
/// Wholesale → Companies (spec 2026-11-23 §3): la empresa del distribuidor en su tenant nuevo, por el puerto de alta del sistema
/// de Companies (<see cref="ICompanyProvisioning"/>), que no pide permiso ni <c>IExecutionContext</c> porque quien acepta todavía
/// no es miembro del tenant. Traduce el borrador campo por campo y no decide nada: la idempotencia (por tipo y número exactos,
/// bajo candado) y las validaciones son de Companies, y sus excepciones salen tal cual.
/// </summary>
internal sealed class WholesaleCompanyProvisioning(ICompanyProvisioning companies) : IWholesaleCompanyProvisioning
{
    public Task<Guid> EnsureCompanyAsync(
        Guid distributorTenantId,
        Guid actorUserId,
        WholesaleCompanyDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return companies.EnsureCompanyAsync(
            distributorTenantId,
            actorUserId,
            new ProvisionedCompanyData(
                draft.Name,
                draft.Country,
                draft.DocumentType,
                draft.TaxId,
                draft.CityId,
                draft.CityName,
                draft.Address,
                draft.Email,
                draft.Phone),
            cancellationToken);
    }
}