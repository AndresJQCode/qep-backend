using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// El histórico de precios se arma comparando el producto que está en la base contra el
/// pricing que llega en el `PUT`, **antes** de aplicarlo: después de <c>Product.Update</c> el
/// valor viejo ya no existe en ningún lado. Estas pruebas fijan qué cuenta como cambio.
/// </summary>
public sealed class ProductPriceChangeDetectorTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid ChangedBy = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DetectReturnsNothingWhenNothingChanged()
    {
        var product = ProductWith(Prices(("USD", 100m), ("COP", 400000m)), ScaleOf(1, 9, 10m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(Prices(("USD", 100m), ("COP", 400000m)), ScaleOf(1, 9, 10m)),
            ChangedBy,
            Now);

        Assert.Empty(changes);
    }

    [Fact]
    public void DetectEmitsARowWhenTheBasePriceInUsdChanges()
    {
        var product = ProductWith(Prices(("USD", 100m)));

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(Prices(("USD", 120m))), ChangedBy, Now));

        Assert.Equal(ProductPriceField.PriceBase, change.Field);
        Assert.Equal("USD", change.Currency);
        Assert.Equal(100m, change.PreviousValue);
        Assert.Equal(120m, change.NewValue);
    }

    [Fact]
    public void DetectEmitsARowWhenTheBasePriceInCopChanges()
    {
        var product = ProductWith(Prices(("COP", 400000m)));

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(Prices(("COP", 450000m))), ChangedBy, Now));

        Assert.Equal(ProductPriceField.PriceBase, change.Field);
        Assert.Equal("COP", change.Currency);
        Assert.Equal(400000m, change.PreviousValue);
        Assert.Equal(450000m, change.NewValue);
    }

    // Un precio que aparece donde no había ninguno es un cambio de precio como cualquier otro:
    // el reporte tiene que poder decir "antes no tenía precio en dólares".
    [Fact]
    public void DetectEmitsARowWhenABasePriceGoesFromNothingToAValue()
    {
        var product = ProductWith(Prices(("COP", 400000m)));

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(Prices(("USD", 100m), ("COP", 400000m))), ChangedBy, Now));

        Assert.Equal(ProductPriceField.PriceBase, change.Field);
        Assert.Equal("USD", change.Currency);
        Assert.Null(change.PreviousValue);
        Assert.Equal(100m, change.NewValue);
    }

    // La vuelta del anterior. `Product.Update` deja limpiar una moneda mientras quede la otra,
    // y borrar un precio también es historia.
    [Fact]
    public void DetectEmitsARowWhenABasePriceGoesFromAValueToNothing()
    {
        var product = ProductWith(Prices(("USD", 100m), ("COP", 400000m)));

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(Prices(("COP", 400000m))), ChangedBy, Now));

        Assert.Equal(ProductPriceField.PriceBase, change.Field);
        Assert.Equal("USD", change.Currency);
        Assert.Equal(100m, change.PreviousValue);
        Assert.Null(change.NewValue);
    }

    // `100m` y `100.00m` son el mismo número con distinta escala decimal. Comparar por valor y
    // no por representación evita una fila de histórico por cada `PUT` que reenvía el mismo
    // precio con otro formato — que es lo que hace un formulario.
    [Fact]
    public void DetectIgnoresADifferenceThatIsOnlyDecimalScale()
    {
        var product = ProductWith(Prices(("USD", 100m)));

        Assert.Empty(ProductPriceChangeDetector.Detect(
            product, PricingOf(Prices(("USD", 100.00m))), ChangedBy, Now));
    }

    [Fact]
    public void DetectEmitsARowWhenTheDiscountOfAnExistingScaleChanges()
    {
        var product = ProductWith(Prices(("USD", 100m)), ScaleOf(1, 9, 10m));

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product,
            PricingOf(Prices(("USD", 100m)), ScaleOf(1, 9, 25m)),
            ChangedBy,
            Now));

        Assert.Equal(ProductPriceField.ScaleDiscount, change.Field);
        Assert.Equal(1, change.ScaleFromUnit);
        Assert.Equal(9, change.ScaleToUnit);
        Assert.Equal(10m, change.PreviousValue);
        Assert.Equal(25m, change.NewValue);
    }

    [Fact]
    public void DetectIgnoresAScaleWhoseDiscountDidNotChange()
    {
        var product = ProductWith(Prices(("USD", 100m)), ScaleOf(1, 9, 10m));

        Assert.Empty(ProductPriceChangeDetector.Detect(
            product,
            PricingOf(Prices(("USD", 100m)), ScaleOf(1, 9, 10m)),
            ChangedBy,
            Now));
    }

    // Un `PUT` reemplaza las escalas enteras y les da ids nuevos, así que el apareo es por
    // rango: una escala con un rango que antes no existía es un alta, no una edición.
    [Fact]
    public void DetectEmitsARowWithoutAPreviousValueWhenAScaleIsAdded()
    {
        var product = ProductWith(Prices(("USD", 100m)), ScaleOf(1, 9, 10m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(Prices(("USD", 100m)), ScaleOf(1, 9, 10m), ScaleOf(10, 50, 30m)),
            ChangedBy,
            Now);

        var added = Assert.Single(changes);
        Assert.Equal(ProductPriceField.ScaleDiscount, added.Field);
        Assert.Equal(10, added.ScaleFromUnit);
        Assert.Equal(50, added.ScaleToUnit);
        Assert.Null(added.PreviousValue);
        Assert.Equal(30m, added.NewValue);
    }

    [Fact]
    public void DetectEmitsARowWithoutANewValueWhenAScaleIsRemoved()
    {
        var product = ProductWith(Prices(("USD", 100m)), ScaleOf(1, 9, 10m), ScaleOf(10, 50, 30m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(Prices(("USD", 100m)), ScaleOf(1, 9, 10m)),
            ChangedBy,
            Now);

        var removed = Assert.Single(changes);
        Assert.Equal(ProductPriceField.ScaleDiscount, removed.Field);
        Assert.Equal(10, removed.ScaleFromUnit);
        Assert.Equal(50, removed.ScaleToUnit);
        Assert.Equal(30m, removed.PreviousValue);
        Assert.Null(removed.NewValue);
    }

    // Un `PUT` toca todo a la vez. Emitir sólo el primer cambio dejaría el histórico
    // silenciosamente incompleto, que es peor que no tenerlo.
    [Fact]
    public void DetectEmitsEveryChangeOfTheSameUpdate()
    {
        var product = ProductWith(
            Prices(("USD", 100m), ("COP", 400000m)),
            ScaleOf(1, 9, 10m),
            ScaleOf(10, 50, 30m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(
                Prices(("USD", 120m), ("COP", 450000m)),
                ScaleOf(1, 9, 15m),
                ScaleOf(60, 100, 40m)),
            ChangedBy,
            Now);

        Assert.Equal(5, changes.Count);
        Assert.Single(changes, change => change.Currency == "USD");
        Assert.Single(changes, change => change.Currency == "COP");

        var edited = Assert.Single(changes, change => change.ScaleFromUnit == 1);
        Assert.Equal(10m, edited.PreviousValue);
        Assert.Equal(15m, edited.NewValue);

        var removed = Assert.Single(changes, change => change.ScaleFromUnit == 10);
        Assert.Equal(30m, removed.PreviousValue);
        Assert.Null(removed.NewValue);

        var added = Assert.Single(changes, change => change.ScaleFromUnit == 60);
        Assert.Null(added.PreviousValue);
        Assert.Equal(40m, added.NewValue);
    }

    // Sin tenant, producto, autor y fecha en cada fila el histórico no se puede reportar ni
    // aislar por tenant, que es de lo que existe.
    [Fact]
    public void DetectStampsTenantProductAuthorAndInstantOnEveryRow()
    {
        var product = ProductWith(Prices(("USD", 100m), ("COP", 400000m)), ScaleOf(1, 9, 10m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(Prices(("USD", 120m), ("COP", 450000m)), ScaleOf(1, 9, 15m)),
            ChangedBy,
            Now);

        Assert.Equal(3, changes.Count);
        Assert.All(changes, change =>
        {
            Assert.Equal(TenantId, change.TenantId);
            Assert.Equal(product.Id, change.ProductId);
            Assert.Equal(ChangedBy, change.ChangedBy);
            Assert.Equal(Now, change.ChangedAt);
            Assert.NotEqual(Guid.Empty, change.Id.Value);
        });
    }

    // El rango sólo tiene sentido para una escala: un precio base es del producto entero. Una
    // fila de base con rango haría que el reporte lo atribuyera a una escala inexistente.
    [Fact]
    public void DetectLeavesTheScaleRangeEmptyOnBasePriceRows()
    {
        var product = ProductWith(Prices(("USD", 100m)));

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(Prices(("USD", 120m))), ChangedBy, Now));

        Assert.Null(change.ScaleFromUnit);
        Assert.Null(change.ScaleToUnit);
    }

    [Fact]
    public void DetectEmitsOnePriceBaseRowPerCurrencyThatChanged()
    {
        var product = ProductWith(Prices(("COP", 400_000m), ("USD", 100m)));

        var changes = ProductPriceChangeDetector.Detect(
            product, PricingOf(Prices(("COP", 450_000m), ("USD", 100m))), ChangedBy, Now);

        var change = Assert.Single(changes);
        Assert.Equal(ProductPriceField.PriceBase, change.Field);
        Assert.Equal("COP", change.Currency);
        Assert.Equal(400_000m, change.PreviousValue);
        Assert.Equal(450_000m, change.NewValue);
    }

    [Fact]
    public void DetectRecordsAnAddedAndARemovedCurrency()
    {
        var product = ProductWith(Prices(("COP", 400_000m)));

        var changes = ProductPriceChangeDetector.Detect(
            product, PricingOf(Prices(("EUR", 95m))), ChangedBy, Now);

        // Ordinal order, so the same PUT always writes the same rows in the same order.
        Assert.Collection(
            changes,
            removed => Assert.Equal(("COP", (decimal?)400_000m, (decimal?)null), (removed.Currency, removed.PreviousValue, removed.NewValue)),
            added => Assert.Equal(("EUR", (decimal?)null, (decimal?)95m), (added.Currency, added.PreviousValue, added.NewValue)));
    }

    [Fact]
    public void AScaleDiscountRowHasNoCurrency()
    {
        var product = ProductWith(Prices(("COP", 100_000m)), ScaleOf(1, 9, 10m));

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(Prices(("COP", 100_000m)), ScaleOf(1, 9, 15m)), ChangedBy, Now));

        Assert.Equal(ProductPriceField.ScaleDiscount, change.Field);
        Assert.Null(change.Currency);
    }

    private static Product ProductWith(
        IReadOnlyDictionary<string, decimal> prices,
        params PriceScaleInput[] scales) =>
        Product.Create(
            ProductId.New(), TenantId, "Vela de soja", "VS-001", ProductDetails.Empty,
            PricingOf(prices, scales), Now);

    private static ProductPricing PricingOf(
        IReadOnlyDictionary<string, decimal> prices,
        params PriceScaleInput[] scales) =>
        new() { Prices = prices, Scales = scales };

    private static Dictionary<string, decimal> Prices(params (string Currency, decimal Amount)[] prices) =>
        prices.ToDictionary(price => price.Currency, price => price.Amount);

    private static PriceScaleInput ScaleOf(int fromUnit, int toUnit, decimal discount) =>
        new(fromUnit, toUnit, discount, PriceScaleRestriction.Multiple, Multiple: 1);
}
