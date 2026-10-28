namespace Modules.Catalog.Domain;

/// <summary>
/// Compara el precio que una variante de un <see cref="Product"/> tiene hoy contra el que trae un
/// <see cref="ProductPricing"/> entrante, y devuelve una fila de histórico por cada valor que
/// cambió. Cada fila lleva la variante cuyo precio cambió (spec 2026-10-20 §3.5): un cambio se
/// registra **por variante**, y una variante cuyo precio no cambió no deja filas.
///
/// **Puro y estático a propósito.** No toca el producto, no lee la base y no depende del reloj
/// ni del usuario —los dos llegan por parámetro—, así que se prueba entero en memoria. Y tiene
/// que correr **antes** de <c>Product.Update</c>: después de aplicarlo el valor viejo ya no
/// existe en ningún lado desde donde recuperarlo.
/// </summary>
public static class ProductPriceChangeDetector
{
    /// <summary>
    /// El historial del PUT con variantes: compara cada variante **guardada** que viene
    /// en el cuerpo contra su propio precio de hoy. Una variante nueva (sin id) no deja filas: nace
    /// con su precio, igual que un producto nuevo no deja historial al crearse. Una variante
    /// guardada que no viene no se compara: el agregado la rechaza o la resuelve por su cuenta.
    /// </summary>
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
        IReadOnlyList<ProductVariantInput> variants,
        Guid changedBy,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(variants);

        // Un diccionario y no FirstOrDefault en el bucle: Product.Variants ordena y materializa en cada
        // lectura, y con 2.048 variantes eso era un ordenamiento entero por fila del PUT.
        var savedById = product.Variants.ToDictionary(saved => saved.Id.Value);
        var changes = new List<ProductPriceChange>();
        foreach (var input in variants)
        {
            if (input.Id is not { } id || !savedById.TryGetValue(id, out var variant))
            {
                continue;
            }

            changes.AddRange(BuildChanges(product, variant, input.Pricing, changedBy, occurredAt));
        }

        return changes;
    }

    /// <summary>
    /// El historial de **una** variante: la que la copia de escalas está por pisar. La copia
    /// trabaja de variante a variantes de uno o varios productos, así que no tiene una lista de
    /// <see cref="ProductVariantInput"/> que comparar sino el <see cref="ProductPricing"/> que
    /// <see cref="PriceScaleCopy.ToPricingFor"/> armó para cada destino. <paramref name="variant"/>
    /// tiene que ser una variante de <paramref name="product"/>: las filas llevan el id de las dos.
    /// </summary>
    public static IReadOnlyList<ProductPriceChange> DetectForVariant(
        Product product,
        ProductVariant variant,
        ProductPricing newPricing,
        Guid changedBy,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(newPricing);

        if (variant.ProductId != product.Id)
        {
            throw new ArgumentException("The variant does not belong to the product.", nameof(variant));
        }

        return BuildChanges(product, variant, newPricing, changedBy, occurredAt);
    }

    private static List<ProductPriceChange> BuildChanges(
        Product product,
        ProductVariant variant,
        ProductPricing newPricing,
        Guid changedBy,
        DateTimeOffset occurredAt)
    {
        var changes = new List<ProductPriceChange>();

        AddBasePriceChanges(changes, product, variant, newPricing, changedBy, occurredAt);
        AddScaleDiscountChanges(changes, product, variant, newPricing, changedBy, occurredAt);

        return changes;
    }

    // One row per currency whose amount changed, appeared or disappeared (spec D3). Ordinal order
    // so the same PUT always writes the same rows. `!=` on decimal? compares by value: 100m and
    // 100.00m are the same price.
    private static void AddBasePriceChanges(
        List<ProductPriceChange> changes,
        Product product,
        ProductVariant variant,
        ProductPricing newPricing,
        Guid changedBy,
        DateTimeOffset occurredAt)
    {
        var currencies = variant.Prices
            .Select(price => price.Currency)
            .Union(newPricing.Prices.Keys)
            .Order(StringComparer.Ordinal);

        foreach (var currency in currencies)
        {
            var previousValue = variant.PriceIn(currency);
            decimal? newValue = newPricing.Prices.TryGetValue(currency, out var amount) ? amount : null;
            if (previousValue == newValue)
            {
                continue;
            }

            changes.Add(ProductPriceChange.ForBasePrice(
                product.TenantId, product.Id, variant.Id, currency, previousValue, newValue, changedBy, occurredAt));
        }
    }

    // Las escalas se aparean por su rango `(FromUnit, ToUnit)` y no por id: un `PUT` las
    // reemplaza enteras y `Product.ApplyPricing` le asigna un `PriceScaleId` nuevo a cada una,
    // así que por id **toda** escala se leería como borrada y vuelta a crear, y el histórico
    // diría que todos los descuentos cambiaron en cada guardado. El rango es lo único que
    // sobrevive al reemplazo.
    private static void AddScaleDiscountChanges(
        List<ProductPriceChange> changes,
        Product product,
        ProductVariant variant,
        ProductPricing newPricing,
        Guid changedBy,
        DateTimeOffset occurredAt)
    {
        var previousDiscounts = IndexByRange(
            variant.PriceScales.Select(scale => (scale.FromUnit, scale.ToUnit, scale.Discount)));
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
                variant.Id,
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
