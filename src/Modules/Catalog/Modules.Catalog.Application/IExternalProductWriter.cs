namespace Modules.Catalog.Application;

/// <summary>Una opción del producto externo, por nombre (spec 2026-11-17 §5.3). Se busca en la biblioteca
/// del tenant sin distinguir mayúsculas ni espacios de borde; si no existe se crea.</summary>
public sealed record ExternalOptionDraft(string Name, IReadOnlyList<string> Values);

/// <summary>Una variante del producto externo.</summary>
/// <param name="Key">El id de la variante en la plataforma externa, como texto; vuelve en
/// <see cref="ExternalProductResult.VariantIdsByKey"/> para que quien llama guarde el vínculo.</param>
/// <param name="OptionValues">Un valor por opción del borrador, en su orden.</param>
/// <param name="Price">El precio en <see cref="ExternalProductDraft.Currency"/>, ya con la regla de
/// impuestos y redondeo aplicada por quien llama. Null sólo al crear (mayorista fase 3, D-16): el producto nace sin
/// precio de venta; <see cref="IExternalProductWriter.UpdateAsync"/> lo rechaza con <see cref="ArgumentException"/>.</param>
/// <param name="Cost">El costo sin IVA en <see cref="ExternalProductDraft.Currency"/> (D-18). Sólo se guarda si es mayor que
/// cero; nulo o no positivo, la variante nace sin costo.</param>
public sealed record ExternalVariantDraft(
    string Key,
    string Code,
    string? Barcode,
    IReadOnlyList<string> OptionValues,
    decimal? Price,
    decimal? Cost = null);

/// <summary>
/// El producto como lo manda la plataforma externa. <paramref name="TaxRateId"/> sólo se usa al
/// crear: después la tasa se edita en QEP (spec 2026-11-17 S-2, plan D4).
/// </summary>
/// <param name="Source">El origen que se guarda en <c>Product.ExternalSource</c> (<c>"shopify"</c>). Null deja el producto
/// propio de QEP (D-17): el que crea Compras lo edita el distribuidor como cualquier otro.</param>
/// <param name="ActorId">Quién firma la auditoría y el historial de precios: el usuario que pidió la
/// importación, o <see cref="Guid.Empty"/> para el sistema (P3).</param>
public sealed record ExternalProductDraft(
    Guid TenantId,
    string? Source,
    string Name,
    bool IsActive,
    Guid? TaxRateId,
    string Currency,
    IReadOnlyList<ExternalOptionDraft> Options,
    IReadOnlyList<ExternalVariantDraft> Variants,
    Guid ActorId,
    DateTimeOffset OccurredAt);

/// <param name="VariantIdsByKey">La variante de QEP de cada <see cref="ExternalVariantDraft.Key"/>.</param>
/// <param name="DroppedBarcodeKeys">Las variantes cuyo código de barras ya usaba otro producto del
/// tenant (o otra variante del borrador): entraron sin él.</param>
/// <param name="Changed">False si el borrador no cambiaba nada y no se guardó (la versión sigue igual).</param>
public sealed record ExternalProductResult(
    Guid ProductId,
    long Version,
    IReadOnlyDictionary<string, Guid> VariantIdsByKey,
    IReadOnlyList<string> DroppedBarcodeKeys,
    bool Changed);

/// <summary>Las tasas por defecto de un producto importado (plan D4); null si el tenant no la tiene.</summary>
public sealed record ExternalDefaultTaxRates(Guid? Vat19Id, Guid? ExemptId);

/// <summary>
/// El camino de sistema para escribir productos que administra otra plataforma (spec 2026-11-17 §5.4),
/// precedente <c>IExternalOrderWriter</c> de Quotations. Reusa los agregados y las reglas del POST y el
/// PUT, sin <c>IExecutionContext</c>: lo autoriza quien llama (la importación o el webhook).
///
/// **No abre transacción**: guarda con <see cref="ICatalogUnitOfWork.SaveChangesAsync"/>, así que quien
/// llama puede tener una abierta y enrolada (el adaptador de Shopify la comparte para que producto,
/// opciones y vínculos se confirmen juntos). Las reglas de dominio salen como
/// <c>CatalogDomainException</c>; traducirlas es de quien llama.
/// </summary>
public interface IExternalProductWriter
{
    Task<ExternalProductResult> CreateAsync(ExternalProductDraft draft, CancellationToken cancellationToken);

    /// <param name="existing">La variante guardada de cada <see cref="ExternalVariantDraft.Key"/> ya
    /// vinculada. Una variante del producto a la que no apunta ninguna del borrador se borra si ninguna
    /// línea la usa y se desactiva si alguna sí (<see cref="IVariantUsageChecker"/>), como el PUT.</param>
    Task<ExternalProductResult> UpdateAsync(
        Guid productId,
        ExternalProductDraft draft,
        IReadOnlyDictionary<string, Guid> existing,
        CancellationToken cancellationToken);

    /// <returns>False si ya estaba así (no se guardó nada).</returns>
    Task<bool> SetActiveAsync(
        Guid tenantId,
        Guid productId,
        bool active,
        Guid actorId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);

    /// <summary>Desvincular (§5.5): el producto queda propio de QEP. Sin efecto si ya lo era o si ya no existe.</summary>
    Task ReleaseAsync(
        Guid tenantId,
        Guid productId,
        Guid actorId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Pone la imagen de la plataforma en lugar de <paramref name="previousFileId"/> (la que puso la
    /// importación), con las reglas de <c>Product.ReplaceGalleryImage</c>. Libera a Storage el archivo
    /// reemplazado o, si el nuevo no entró, el nuevo: si no, quedaría un archivo sin dueño para siempre.
    /// </summary>
    /// <returns>True si el archivo nuevo quedó en la galería.</returns>
    Task<bool> ReplaceImageAsync(
        Guid tenantId,
        Guid productId,
        Guid? previousFileId,
        Guid newFileId,
        Guid actorId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken);

    Task<ExternalDefaultTaxRates> FindDefaultTaxRatesAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>
    /// La variante del tenant con ese código (D-18): recortado, igualdad exacta con mayúsculas como el índice único, y en
    /// cualquier estado del producto y de la variante. Null si no hay, o si el código queda vacío.
    /// </summary>
    Task<Guid?> FindVariantIdByCodeAsync(Guid tenantId, string code, CancellationToken cancellationToken);
}
