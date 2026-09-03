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
        var product = ProductWith(baseUsd: 100m, baseCop: 400000m, ScaleOf(100m, 1, 9, 10m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(100m, 400000m, ScaleOf(100m, 1, 9, 10m)),
            ChangedBy,
            Now);

        Assert.Empty(changes);
    }

    [Fact]
    public void DetectEmitsARowWhenTheBasePriceInUsdChanges()
    {
        var product = ProductWith(baseUsd: 100m, baseCop: null);

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(120m, null), ChangedBy, Now));

        Assert.Equal(ProductPriceField.PriceBaseUsd, change.Field);
        Assert.Equal(100m, change.PreviousValue);
        Assert.Equal(120m, change.NewValue);
    }

    [Fact]
    public void DetectEmitsARowWhenTheBasePriceInCopChanges()
    {
        var product = ProductWith(baseUsd: null, baseCop: 400000m);

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(null, 450000m), ChangedBy, Now));

        Assert.Equal(ProductPriceField.PriceBaseCop, change.Field);
        Assert.Equal(400000m, change.PreviousValue);
        Assert.Equal(450000m, change.NewValue);
    }

    // Un precio que aparece donde no había ninguno es un cambio de precio como cualquier otro:
    // el reporte tiene que poder decir "antes no tenía precio en dólares".
    [Fact]
    public void DetectEmitsARowWhenABasePriceGoesFromNothingToAValue()
    {
        var product = ProductWith(baseUsd: null, baseCop: 400000m);

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(100m, 400000m), ChangedBy, Now));

        Assert.Equal(ProductPriceField.PriceBaseUsd, change.Field);
        Assert.Null(change.PreviousValue);
        Assert.Equal(100m, change.NewValue);
    }

    // La vuelta del anterior. `Product.Update` deja limpiar una moneda mientras quede la otra,
    // y borrar un precio también es historia.
    [Fact]
    public void DetectEmitsARowWhenABasePriceGoesFromAValueToNothing()
    {
        var product = ProductWith(baseUsd: 100m, baseCop: 400000m);

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(null, 400000m), ChangedBy, Now));

        Assert.Equal(ProductPriceField.PriceBaseUsd, change.Field);
        Assert.Equal(100m, change.PreviousValue);
        Assert.Null(change.NewValue);
    }

    // `100m` y `100.00m` son el mismo número con distinta escala decimal. Comparar por valor y
    // no por representación evita una fila de histórico por cada `PUT` que reenvía el mismo
    // precio con otro formato — que es lo que hace un formulario.
    [Fact]
    public void DetectIgnoresADifferenceThatIsOnlyDecimalScale()
    {
        var product = ProductWith(baseUsd: 100m, baseCop: null);

        Assert.Empty(ProductPriceChangeDetector.Detect(
            product, PricingOf(100.00m, null), ChangedBy, Now));
    }

    [Fact]
    public void DetectEmitsARowWhenTheDiscountOfAnExistingScaleChanges()
    {
        var product = ProductWith(baseUsd: 100m, baseCop: null, ScaleOf(100m, 1, 9, 10m));

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product,
            PricingOf(100m, null, ScaleOf(100m, 1, 9, 25m)),
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
        var product = ProductWith(baseUsd: 100m, baseCop: null, ScaleOf(100m, 1, 9, 10m));

        Assert.Empty(ProductPriceChangeDetector.Detect(
            product,
            PricingOf(100m, null, ScaleOf(100m, 1, 9, 10m)),
            ChangedBy,
            Now));
    }

    // Un `PUT` reemplaza las escalas enteras y les da ids nuevos, así que el apareo es por
    // rango: una escala con un rango que antes no existía es un alta, no una edición.
    [Fact]
    public void DetectEmitsARowWithoutAPreviousValueWhenAScaleIsAdded()
    {
        var product = ProductWith(baseUsd: 100m, baseCop: null, ScaleOf(100m, 1, 9, 10m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(100m, null, ScaleOf(100m, 1, 9, 10m), ScaleOf(100m, 10, 50, 30m)),
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
        var product = ProductWith(
            baseUsd: 100m, baseCop: null, ScaleOf(100m, 1, 9, 10m), ScaleOf(100m, 10, 50, 30m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(100m, null, ScaleOf(100m, 1, 9, 10m)),
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
            baseUsd: 100m,
            baseCop: 400000m,
            ScaleOf(100m, 1, 9, 10m),
            ScaleOf(100m, 10, 50, 30m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(
                120m,
                450000m,
                ScaleOf(120m, 1, 9, 15m),
                ScaleOf(120m, 60, 100, 40m)),
            ChangedBy,
            Now);

        Assert.Equal(5, changes.Count);
        Assert.Single(changes, change => change.Field == ProductPriceField.PriceBaseUsd);
        Assert.Single(changes, change => change.Field == ProductPriceField.PriceBaseCop);

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
        var product = ProductWith(baseUsd: 100m, baseCop: 400000m, ScaleOf(100m, 1, 9, 10m));

        var changes = ProductPriceChangeDetector.Detect(
            product,
            PricingOf(120m, 450000m, ScaleOf(120m, 1, 9, 15m)),
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
        var product = ProductWith(baseUsd: 100m, baseCop: null);

        var change = Assert.Single(ProductPriceChangeDetector.Detect(
            product, PricingOf(120m, null), ChangedBy, Now));

        Assert.Null(change.ScaleFromUnit);
        Assert.Null(change.ScaleToUnit);
    }

    private static Product ProductWith(
        decimal? baseUsd,
        decimal? baseCop,
        params PriceScaleInput[] scales) =>
        Product.Create(
            ProductId.New(),
            TenantId,
            "Vela de soja",
            "VS-001",
            ProductDetails.Empty,
            PricingOf(baseUsd, baseCop, scales),
            Now);

    private static ProductPricing PricingOf(
        decimal? baseUsd,
        decimal? baseCop,
        params PriceScaleInput[] scales) =>
        new() { BaseUsd = baseUsd, BaseCop = baseCop, Scales = scales };

    // El precio final lo manda el cliente y el dominio lo valida contra base × (1 − descuento),
    // así que las escalas de estas pruebas tienen que traerlo calculado o `Product.Create`
    // rechaza el producto antes de que el detector llegue a correr.
    private static PriceScaleInput ScaleOf(
        decimal baseUsd,
        int fromUnit,
        int toUnit,
        decimal discount) =>
        new(
            fromUnit,
            toUnit,
            discount,
            PriceScaleRestriction.Multiple,
            Multiple: 1,
            PackagingUnit: null,
            FinalUsd: Math.Round(
                baseUsd * (1 - discount / 100m), 2, MidpointRounding.AwayFromZero),
            FinalCop: null);
}
