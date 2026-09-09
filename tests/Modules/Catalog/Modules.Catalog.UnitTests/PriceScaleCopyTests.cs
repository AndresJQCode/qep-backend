using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// La copia de escalas entre productos, del lado del dominio.
///
/// El caso que le da sentido a todo esto es <see cref="RecalculatesTheFinalPriceAgainstTheTarget"/>:
/// arrastrar el precio final del origen —lo que hacía el lote armado desde el navegador— rompía
/// contra cualquier destino con otro precio base, porque <c>PriceScale.Create</c> valida el final
/// contra el precio base del producto dueño.
/// </summary>
public sealed class PriceScaleCopyTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now =
        new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static Product ProductWith(ProductPricing pricing, string code = "VS-001") =>
        Product.Create(
            ProductId.New(), TenantId, "Vela de soja", code, ProductDetails.Empty, pricing, Now);

    private static PriceScaleInput MultipleScale(
        int fromUnit = 1, int toUnit = 9, decimal discount = 10m,
        int multiple = 3, decimal? finalUsd = 90m, decimal? finalCop = null,
        bool allowGrouping = false) =>
        new(
            fromUnit, toUnit, discount,
            PriceScaleRestriction.Multiple, multiple, null, finalUsd, finalCop, allowGrouping);

    [Fact]
    public void RecalculatesTheFinalPriceAgainstTheTarget()
    {
        // Origen a 100 USD con 10% de descuento => final 90. El destino vale 50, así que su
        // final tiene que ser 45 y no los 90 del origen.
        var source = ProductWith(new ProductPricing
        {
            BaseUsd = 100m,
            Scales = [MultipleScale(discount: 10m, finalUsd: 90m)]
        });
        var target = ProductWith(new ProductPricing { BaseUsd = 50m }, "VS-002");

        var pricing = PriceScaleCopy.ToPricingFor(source, target);
        target.ApplyPriceScales(pricing.Scales, Now.AddMinutes(5));

        var copied = Assert.Single(target.PriceScales);
        Assert.Equal(45m, copied.FinalUsd);
        Assert.Equal(10m, copied.Discount);
    }

    // El precio base del destino no es cosa de esta operación: se copian las escalas, no el precio.
    [Fact]
    public void LeavesTheTargetBasePricesAlone()
    {
        var source = ProductWith(new ProductPricing
        {
            BaseUsd = 100m,
            Scales = [MultipleScale()]
        });
        var target = ProductWith(
            new ProductPricing { BaseUsd = 50m, BaseCop = 200000m }, "VS-002");

        var pricing = PriceScaleCopy.ToPricingFor(source, target);
        target.ApplyPriceScales(pricing.Scales, Now.AddMinutes(5));

        Assert.Equal(50m, target.PriceBaseUsd);
        Assert.Equal(200000m, target.PriceBaseCop);
        Assert.Equal(50m, pricing.BaseUsd);
        Assert.Equal(200000m, pricing.BaseCop);
    }

    // El dominio prohíbe un precio final en una moneda sin precio base en esa moneda. Un destino
    // que sólo tiene USD recibe la escala igual, con el final en COP vacío.
    [Fact]
    public void LeavesAFinalPriceEmptyWhenTheTargetHasNoBaseInThatCurrency()
    {
        var source = ProductWith(new ProductPricing
        {
            BaseUsd = 100m,
            BaseCop = 400000m,
            Scales = [MultipleScale(discount: 10m, finalUsd: 90m, finalCop: 360000m)]
        });
        var target = ProductWith(new ProductPricing { BaseUsd = 50m }, "VS-002");

        var pricing = PriceScaleCopy.ToPricingFor(source, target);
        target.ApplyPriceScales(pricing.Scales, Now.AddMinutes(5));

        var copied = Assert.Single(target.PriceScales);
        Assert.Equal(45m, copied.FinalUsd);
        Assert.Null(copied.FinalCop);
    }

    [Fact]
    public void CopiesTheShapeOfEveryScale()
    {
        var source = ProductWith(new ProductPricing
        {
            BaseUsd = 100m,
            Scales =
            [
                MultipleScale(fromUnit: 10, toUnit: 20, discount: 20m, multiple: 5,
                    finalUsd: 80m, allowGrouping: true),
                new PriceScaleInput(
                    1, 9, 10m, PriceScaleRestriction.PackagingUnit, null, 12, 90m, null)
            ]
        });
        var target = ProductWith(new ProductPricing { BaseUsd = 100m }, "VS-002");

        target.ApplyPriceScales(
            PriceScaleCopy.ToPricingFor(source, target).Scales, Now.AddMinutes(5));

        // Ordenadas por rango, no en el orden en que venían.
        var scales = target.PriceScales.ToArray();
        Assert.Equal(2, scales.Length);

        Assert.Equal(1, scales[0].FromUnit);
        Assert.Equal(9, scales[0].ToUnit);
        Assert.Equal(PriceScaleRestriction.PackagingUnit, scales[0].Restriction);
        Assert.Equal(12, scales[0].PackagingUnit);
        Assert.Null(scales[0].Multiple);
        Assert.False(scales[0].AllowGrouping);

        Assert.Equal(10, scales[1].FromUnit);
        Assert.Equal(20, scales[1].ToUnit);
        Assert.Equal(PriceScaleRestriction.Multiple, scales[1].Restriction);
        Assert.Equal(5, scales[1].Multiple);
        Assert.Null(scales[1].PackagingUnit);
        Assert.True(scales[1].AllowGrouping);
    }

    // Reemplazo, no suma: es lo que la pantalla avisa antes de copiar.
    [Fact]
    public void ReplacesTheScalesTheTargetAlreadyHad()
    {
        var source = ProductWith(new ProductPricing
        {
            BaseUsd = 100m,
            Scales = [MultipleScale(fromUnit: 10, toUnit: 20)]
        });
        var target = ProductWith(
            new ProductPricing { BaseUsd = 100m, Scales = [MultipleScale(fromUnit: 1, toUnit: 9)] },
            "VS-002");

        target.ApplyPriceScales(
            PriceScaleCopy.ToPricingFor(source, target).Scales, Now.AddMinutes(5));

        var copied = Assert.Single(target.PriceScales);
        Assert.Equal(10, copied.FromUnit);
    }

    // Las escalas copiadas son filas nuevas, igual que en un PUT: el id no viaja desde el origen.
    [Fact]
    public void GivesEveryCopiedScaleItsOwnId()
    {
        var source = ProductWith(new ProductPricing
        {
            BaseUsd = 100m,
            Scales = [MultipleScale()]
        });
        var target = ProductWith(new ProductPricing { BaseUsd = 100m }, "VS-002");
        var sourceScaleId = source.PriceScales.Single().Id;

        target.ApplyPriceScales(
            PriceScaleCopy.ToPricingFor(source, target).Scales, Now.AddMinutes(5));

        var copied = Assert.Single(target.PriceScales);
        Assert.NotEqual(sourceScaleId, copied.Id);
        Assert.Equal(target.Id, copied.ProductId);
        Assert.Equal(TenantId, copied.TenantId);
    }

    [Fact]
    public void StampsTheTargetAsModified()
    {
        var source = ProductWith(new ProductPricing
        {
            BaseUsd = 100m,
            Scales = [MultipleScale()]
        });
        var target = ProductWith(new ProductPricing { BaseUsd = 100m }, "VS-002");
        var version = target.Version;
        var modifiedAt = Now.AddMinutes(5);

        target.ApplyPriceScales(
            PriceScaleCopy.ToPricingFor(source, target).Scales, modifiedAt);

        Assert.Equal(version + 1, target.Version);
        Assert.Equal(modifiedAt, target.UpdatedAt);
    }

    // Misma guarda que Update: un producto inactivo no se edita, y copiarle escalas es editarlo.
    [Fact]
    public void RejectsAnInactiveTarget()
    {
        var source = ProductWith(new ProductPricing
        {
            BaseUsd = 100m,
            Scales = [MultipleScale()]
        });
        var target = ProductWith(new ProductPricing { BaseUsd = 100m }, "VS-002");
        target.Deactivate(Now.AddMinutes(1));

        var error = Assert.Throws<CatalogDomainException>(() =>
            target.ApplyPriceScales(
                PriceScaleCopy.ToPricingFor(source, target).Scales, Now.AddMinutes(5)));

        Assert.Equal("catalog.product.inactive", error.Code);
    }

    // El redondeo tiene que ser el mismo con el que PriceScale.Create acepta o rechaza, o la copia
    // generaría escalas que el propio dominio no admite. round(99.99 × 0.6667, 2) = 66.66.
    [Fact]
    public void RoundsTheFinalPriceTheSameWayTheValidationDoes()
    {
        var source = ProductWith(new ProductPricing
        {
            BaseUsd = 10m,
            Scales = [MultipleScale(discount: 33.33m, finalUsd: 6.67m)]
        });
        var target = ProductWith(new ProductPricing { BaseUsd = 99.99m }, "VS-002");

        target.ApplyPriceScales(
            PriceScaleCopy.ToPricingFor(source, target).Scales, Now.AddMinutes(5));

        var copied = Assert.Single(target.PriceScales);
        Assert.Equal(66.66m, copied.FinalUsd);
    }
}
