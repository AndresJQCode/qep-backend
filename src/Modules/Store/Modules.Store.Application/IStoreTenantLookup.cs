namespace Modules.Store.Application;

/// <summary>
/// Lo que la tienda necesita saber del tenant sin referenciar Tenancy más allá de <c>Modules.Tenancy.Application</c>: el nombre que
/// pinta cuando no tiene uno propio, su país, si sigue activo, el logo público y el formato regional (el de <c>/auth/me</c>).
/// </summary>
public interface IStoreTenantLookup
{
    /// <summary>Null cuando el tenant no tiene fila (sólo el tenant simulado del stub de desarrollo).</summary>
    Task<StoreTenantRef?> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed record StoreTenantRef(
    string DisplayName,
    string Country,
    bool IsActive,
    string? LogoUrl,
    StoreRegionalFormat Regional);

public sealed record StoreRegionalFormat(
    string TimeZone,
    string DateFormat,
    string NumberFormat,
    string DefaultCurrency,
    string Country);
