namespace Modules.Catalog.Domain;

/// <summary>
/// Compara el precio que un <see cref="Product"/> tiene hoy contra el que trae un
/// <see cref="ProductPricing"/> entrante, y devuelve una fila de histórico por cada valor que
/// cambió.
///
/// **Puro y estático a propósito.** No toca el producto, no lee la base y no depende del reloj
/// ni del usuario —los dos llegan por parámetro—, así que se prueba entero en memoria. Y tiene
/// que correr **antes** de <c>Product.Update</c>: después de aplicarlo el valor viejo ya no
/// existe en ningún lado desde donde recuperarlo.
/// </summary>
public static class ProductPriceChangeDetector
{
    /// <param name="changedBy">
    /// El sujeto que hace el cambio, que el caso de uso saca de <c>IExecutionContext</c>. No lo
    /// resuelve el dominio: el dominio no sabe quién está pidiendo nada.
    /// </param>
    /// <param name="occurredAt">
    /// El mismo instante con el que se va a sellar el <c>Update</c>, no un
    /// <c>DateTimeOffset.UtcNow</c> de adentro: dos relojes distintos dejarían el histórico y el
    /// producto discrepando por milisegundos.
    /// </param>
    public static IReadOnlyList<ProductPriceChange> Detect(
        Product product,
        ProductPricing newPricing,
        Guid changedBy,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(newPricing);

        var changes = new List<ProductPriceChange>();

        AddBasePriceChange(
            changes,
            product,
            ProductPriceField.PriceBaseUsd,
            product.PriceBaseUsd,
            newPricing.BaseUsd,
            changedBy,
            occurredAt);
        AddBasePriceChange(
            changes,
            product,
            ProductPriceField.PriceBaseCop,
            product.PriceBaseCop,
            newPricing.BaseCop,
            changedBy,
            occurredAt);
        AddScaleDiscountChanges(changes, product, newPricing, changedBy, occurredAt);

        return changes;
    }

    // `!=` sobre `decimal?` compara por valor y no por representación: 100m y 100.00m son el
    // mismo número con distinta escala decimal, y un formulario que reenvía el precio con otro
    // formato no cambió el precio. Los dos null tampoco son un cambio; uno solo sí lo es.
    private static void AddBasePriceChange(
        List<ProductPriceChange> changes,
        Product product,
        ProductPriceField field,
        decimal? previousValue,
        decimal? newValue,
        Guid changedBy,
        DateTimeOffset occurredAt)
    {
        if (previousValue == newValue)
        {
            return;
        }

        changes.Add(ProductPriceChange.ForBasePrice(
            product.TenantId,
            product.Id,
            field,
            previousValue,
            newValue,
            changedBy,
            occurredAt));
    }

    // Las escalas se aparean por su rango `(FromUnit, ToUnit)` y no por id: un `PUT` las
    // reemplaza enteras y `Product.ApplyPricing` le asigna un `PriceScaleId` nuevo a cada una,
    // así que por id **toda** escala se leería como borrada y vuelta a crear, y el histórico
    // diría que todos los descuentos cambiaron en cada guardado. El rango es lo único que
    // sobrevive al reemplazo.
    private static void AddScaleDiscountChanges(
        List<ProductPriceChange> changes,
        Product product,
        ProductPricing newPricing,
        Guid changedBy,
        DateTimeOffset occurredAt)
    {
        var previousDiscounts = IndexByRange(
            product.PriceScales.Select(scale => (scale.FromUnit, scale.ToUnit, scale.Discount)));
        var newDiscounts = IndexByRange(
            newPricing.Scales.Select(scale => (scale.FromUnit, scale.ToUnit, scale.Discount)));

        // Ordenado por rango para que el histórico de un mismo `PUT` salga siempre igual: sin
        // esto el orden lo decidiría el hash de la tupla, y una prueba que hoy pasa fallaría
        // mañana sin que nadie toque nada.
        var ranges = previousDiscounts.Keys
            .Union(newDiscounts.Keys)
            .OrderBy(range => range.FromUnit)
            .ThenBy(range => range.ToUnit);

        foreach (var range in ranges)
        {
            var hadBefore = previousDiscounts.TryGetValue(range, out var previousDiscount);
            var hasNow = newDiscounts.TryGetValue(range, out var newDiscount);

            // El rango existe de los dos lados y el descuento es el mismo. Comparado por valor,
            // igual que los precios base.
            if (hadBefore && hasNow && previousDiscount == newDiscount)
            {
                continue;
            }

            changes.Add(ProductPriceChange.ForScaleDiscount(
                product.TenantId,
                product.Id,
                range.FromUnit,
                range.ToUnit,
                // Un rango nuevo entra con `PreviousValue` nulo; uno que desapareció sale con
                // `NewValue` nulo. Las dos cosas son historia de precio y por eso son filas.
                hadBefore ? previousDiscount : null,
                hasNow ? newDiscount : null,
                changedBy,
                occurredAt));
        }
    }

    // Asignación por indexador y no `ToDictionary`: el dominio no prohíbe dos escalas con el
    // mismo rango —`PriceScale.Create` sólo valida que `ToUnit > FromUnit`—, y `ToDictionary`
    // tiraría una excepción desde adentro del `PUT` por un dato que ya está guardado. Ante
    // rangos repetidos gana el último, que es la escala que el cliente mandó al final.
    private static Dictionary<(int FromUnit, int ToUnit), decimal> IndexByRange(
        IEnumerable<(int FromUnit, int ToUnit, decimal Discount)> scales)
    {
        var indexed = new Dictionary<(int FromUnit, int ToUnit), decimal>();
        foreach (var (fromUnit, toUnit, discount) in scales)
        {
            indexed[(fromUnit, toUnit)] = discount;
        }

        return indexed;
    }
}
