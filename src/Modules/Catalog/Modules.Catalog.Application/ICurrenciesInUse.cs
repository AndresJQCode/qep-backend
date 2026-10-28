namespace Modules.Catalog.Application;

/// <summary>
/// Currencies with at least one product price in the tenant, in catalogue order (spec D10).
/// A port and not a repository query because /auth/me (src/Api) and the Excel export need it
/// without loading products.
/// </summary>
public interface ICurrenciesInUse
{
    Task<IReadOnlyList<string>> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
