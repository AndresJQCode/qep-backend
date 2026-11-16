namespace Modules.Support.Application;

/// <summary>A tenant company as Support shows it (spec 2026-11-15 §9.3, plan L30): <see cref="DocumentTypeCode"/> is the wire code
/// (<c>NIT</c>, <c>CC</c>, <c>CE</c>, <c>PASAPORTE</c>, <c>NIF</c>, <c>NIE</c>, <c>TAX_ID</c>) and <see cref="DocumentNumber"/> the
/// company's tax id.</summary>
public sealed record SupportOrganizationRef(Guid Id, string Name, string DocumentTypeCode, string DocumentNumber, bool IsActive);

/// <summary>
/// The tenant companies the public form can show as the one that answers (plan L30). Implemented in Bootstrapper over Companies:
/// Support never references <c>Modules.Companies.*</c> (<c>SupportLayerTests</c>), same pattern as POS and Quotations.
/// </summary>
public interface ISupportOrganizationDirectory
{
    /// <summary>The active companies of the tenant, ordered by name.</summary>
    Task<IReadOnlyList<SupportOrganizationRef>> ListActiveAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The company if it is of the tenant, active or not; null otherwise.</summary>
    Task<SupportOrganizationRef?> FindAsync(Guid tenantId, Guid companyId, CancellationToken cancellationToken);
}
