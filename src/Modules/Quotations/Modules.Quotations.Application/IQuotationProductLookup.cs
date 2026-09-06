namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia Catalog para poner nombre, portada y escalas en cada línea de una cotización ya
/// guardada. Mismo criterio de aislamiento que <see cref="IQuotationCustomerLookup"/>: el
/// adaptador vive en <c>Bootstrapper</c>.
///
/// Separado de <see cref="IQuotationProductPricingLookup"/> a propósito: aquél resuelve **qué
/// precio** aplica al agregar una línea (una escritura, un producto por vez); éste resuelve
/// **cómo se muestra** lo ya guardado, en lote y por pantalla. Trae también los inactivos: una
/// línea vieja puede referenciar un producto que se dio de baja después, y su nombre sigue
/// haciendo falta.
/// </summary>
public interface IQuotationProductLookup
{
    Task<IReadOnlyDictionary<Guid, QuotationProductRef>> FindManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken);
}

public sealed record QuotationProductRef(
    Guid Id,
    string Name,
    string Code,
    /// <summary>URL pública de la portada, ya resuelta por Catalog. Null si no tiene.</summary>
    string? ImageUrl,
    IReadOnlyCollection<QuotationPriceScaleRef> Scales);
