using BuildingBlocks.Domain.Pricing;

namespace Modules.Tenancy.Application;

/// <summary>
/// La regla de redondeo de precios de un tenant, para los módulos de negocio (spec 2026-10-27).
/// Un tenant que no existe en <c>tenancy.tenants</c> no lanza: devuelve los valores por defecto,
/// igual que <c>GetRegionalSettingsAsync</c> da <c>null</c> para el tenant simulado del stub.
/// </summary>
public interface ITenantPriceRounding
{
    /// <summary>La regla efectiva de una moneda del catálogo; sin fila, <see cref="PriceRoundingDefaults.For"/>.</summary>
    Task<PriceRounding> GetAsync(Guid tenantId, string currency, CancellationToken cancellationToken);

    /// <summary>Todas las monedas del catálogo con su regla efectiva, también las que usan el valor por defecto.</summary>
    Task<IReadOnlyDictionary<string, PriceRounding>> GetAllAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>El valor de una moneda que el tenant no configuró: los decimales del catálogo, al más cercano.</summary>
public static class PriceRoundingDefaults
{
    public static PriceRounding For(string currency) =>
        new(Currencies.Get(currency).Decimals, PriceRoundingMode.Nearest);
}
