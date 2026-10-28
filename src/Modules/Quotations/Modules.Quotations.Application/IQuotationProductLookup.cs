namespace Modules.Quotations.Application;

/// <summary>
/// Puerto hacia Catalog para poner nombre, portada y escalas en cada línea de una cotización ya
/// guardada. Mismo criterio de aislamiento que <see cref="IQuotationCustomerLookup"/>: el
/// adaptador vive en <c>Bootstrapper</c>.
///
/// Separado de <see cref="IQuotationProductPricingLookup"/> a propósito: aquél resuelve **qué
/// precio** aplica al agregar una línea (una escritura, una variante por vez); éste resuelve
/// **cómo se muestra** lo ya guardado, en lote y por pantalla. Trae también las inactivas
/// —variante o producto—: una línea vieja puede referenciar algo que se dio de baja después, y su
/// nombre sigue haciendo falta.
///
/// Por **variante** (spec 2026-10-20 §4): una línea vende una variante, y su código, su nombre
/// visible, su foto y sus escalas son de ella.
/// </summary>
public interface IQuotationProductLookup
{
    /// <summary>Por id de variante; un id que no existe en el tenant no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, QuotationVariantRef>> FindManyAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> variantIds,
        CancellationToken cancellationToken);
}

/// <param name="ProductId">El producto dueño: las líneas lo guardan junto a la variante para que
/// un reporte pueda agrupar por producto (spec §4).</param>
/// <param name="Name">El nombre visible ya armado (spec §3.4): el del producto en la variante por
/// defecto, «{producto} – {v1} / {v2}» con opciones. Quotations no lo concatena. Puede pasar de
/// <c>QuotationProductSnapshot.NameMaxLength</c>: el producto llega a 200 y cada valor suma.</param>
/// <param name="Code">El <c>Code</c> de la variante.</param>
/// <param name="ImageUrl">URL pública de la foto de la variante, o de la principal del producto
/// si la variante no eligió ninguna. Null si no hay ninguna disponible del tenant.</param>
public sealed record QuotationVariantRef(
    Guid VariantId,
    Guid ProductId,
    string Name,
    string Code,
    string? ImageUrl,
    IReadOnlyCollection<QuotationPriceScaleRef> Scales);
