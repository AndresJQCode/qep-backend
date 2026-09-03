namespace Modules.Catalog.Domain;

/// <summary>
/// Una fila del histórico de precios de un producto: qué campo cambió, de qué valor a cuál,
/// quién lo cambió y cuándo.
///
/// **Append-only.** No tiene ningún método de mutación y nadie la borra: el histórico es la
/// evidencia de lo que pasó, y una fila editable no sirve de evidencia. Por eso tampoco lleva
/// <c>Version</c> —no hay dos escrituras que se puedan pisar— ni <c>UpdatedAt</c>.
///
/// No es un agregado con repositorio propio: nace dentro de la misma transacción que el
/// <see cref="Product"/> que la origina, y quien la produce es
/// <see cref="ProductPriceChangeDetector"/>.
/// </summary>
public sealed class ProductPriceChange
{
    // EF Core materializa por acá. El código nunca la construye así: Create es el único punto
    // de entrada, igual que en Product.
    private ProductPriceChange() { }

    private ProductPriceChange(
        ProductPriceChangeId id,
        Guid tenantId,
        ProductId productId,
        ProductPriceField field,
        int? scaleFromUnit,
        int? scaleToUnit,
        decimal? previousValue,
        decimal? newValue,
        Guid changedBy,
        DateTimeOffset changedAt)
    {
        Id = id;
        TenantId = tenantId;
        ProductId = productId;
        Field = field;
        ScaleFromUnit = scaleFromUnit;
        ScaleToUnit = scaleToUnit;
        PreviousValue = previousValue;
        NewValue = newValue;
        ChangedBy = changedBy;
        ChangedAt = changedAt;
    }

    public ProductPriceChangeId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public ProductId ProductId { get; private set; }

    public ProductPriceField Field { get; private set; }

    /// <summary>
    /// Qué escala cambió, identificada por su rango. No nulo **sólo** cuando
    /// <see cref="Field"/> es <see cref="ProductPriceField.ScaleDiscount"/>: los precios base
    /// son del producto entero y no tienen rango.
    ///
    /// El rango y no el <see cref="PriceScaleId"/>: un <c>PUT</c> reemplaza las escalas enteras
    /// y les asigna ids nuevos, así que un id apuntaría a una fila que ya no existe. El par
    /// <c>(FromUnit, ToUnit)</c> es lo único que sobrevive al reemplazo, y es por lo que
    /// <see cref="ProductPriceChangeDetector"/> aparea antes y después.
    /// </summary>
    public int? ScaleFromUnit { get; private set; }

    /// <summary>Ver <see cref="ScaleFromUnit"/>.</summary>
    public int? ScaleToUnit { get; private set; }

    /// <summary>
    /// El valor de antes, o <c>null</c> si no había: el precio base estaba vacío, o la escala
    /// no existía antes de este cambio.
    /// </summary>
    public decimal? PreviousValue { get; private set; }

    /// <summary>
    /// El valor de después, o <c>null</c> si dejó de haberlo: el precio base se limpió, o la
    /// escala desapareció en este cambio.
    /// </summary>
    public decimal? NewValue { get; private set; }

    public Guid ChangedBy { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }

    /// <summary>
    /// Una fila de cambio de uno de los dos precios base del producto.
    ///
    /// <c>internal</c> a propósito, igual que <c>PriceScale.Create</c>: el único que decide qué
    /// cambió es <see cref="ProductPriceChangeDetector"/>, y dejar que un caso de uso arme filas
    /// a mano abriría la puerta a un histórico que no se corresponde con lo que se guardó.
    /// </summary>
    internal static ProductPriceChange ForBasePrice(
        Guid tenantId,
        ProductId productId,
        ProductPriceField field,
        decimal? previousValue,
        decimal? newValue,
        Guid changedBy,
        DateTimeOffset changedAt) =>
        new(
            ProductPriceChangeId.New(),
            tenantId,
            productId,
            field,
            scaleFromUnit: null,
            scaleToUnit: null,
            previousValue,
            newValue,
            changedBy,
            changedAt);

    /// <summary>
    /// Una fila de cambio del descuento de una escala. Ver <see cref="ForBasePrice"/> sobre por
    /// qué es <c>internal</c>, y <see cref="ScaleFromUnit"/> sobre por qué identifica la escala
    /// por su rango.
    /// </summary>
    internal static ProductPriceChange ForScaleDiscount(
        Guid tenantId,
        ProductId productId,
        int scaleFromUnit,
        int scaleToUnit,
        decimal? previousValue,
        decimal? newValue,
        Guid changedBy,
        DateTimeOffset changedAt) =>
        new(
            ProductPriceChangeId.New(),
            tenantId,
            productId,
            ProductPriceField.ScaleDiscount,
            scaleFromUnit,
            scaleToUnit,
            previousValue,
            newValue,
            changedBy,
            changedAt);
}
