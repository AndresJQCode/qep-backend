using Modules.Shopify.Application;

namespace Modules.Shopify.UnitTests;

/// <summary>Spec 2026-11-17 §5.3, §10 P6 y P9: qué variantes de Shopify casan por SKU con variantes que ya existen en QEP.</summary>
public sealed class ShopifyProductMatcherTests
{
    private static readonly Guid ProductA = Guid.CreateVersion7();
    private static readonly Guid ProductB = Guid.CreateVersion7();

    private static readonly HashSet<Guid> NoImported = [];

    private static ShopifyVariantSnapshot Variant(long id, string? sku) => new(id, sku, null, 1m, true, []);

    private static ShopifyProductSnapshot Product(params ShopifyVariantSnapshot[] variants) =>
        new(7001L, "Camiseta", ShopifyProductStatus.Active, DateTimeOffset.UnixEpoch, [], variants, null);

    private static Dictionary<string, ShopifyProductRef> Catalog(params (string Code, Guid ProductId)[] variants) =>
        variants.ToDictionary(
            variant => variant.Code,
            variant => new ShopifyProductRef(Guid.CreateVersion7(), variant.ProductId, variant.Code, true, 19),
            StringComparer.Ordinal);

    [Fact]
    public void NoVariantMatchingIsNone()
    {
        var match = ShopifyProductMatcher.Match(Product(Variant(1, "X"), Variant(2, "Y")), Catalog(("Z", ProductA)), NoImported);

        Assert.Equal(ShopifyMatchKind.None, match.Kind);
        Assert.Null(match.QepProductId);
        Assert.Empty(match.Matched);
    }

    [Fact]
    public void AllVariantsMatchingOneProductIsAll()
    {
        var catalog = Catalog(("A-1", ProductA), ("A-2", ProductA));

        var match = ShopifyProductMatcher.Match(Product(Variant(1, " A-1 "), Variant(2, "A-2")), catalog, NoImported);

        Assert.Equal((ShopifyMatchKind.All, (Guid?)ProductA), (match.Kind, match.QepProductId));
        Assert.Equal(catalog["A-1"].VariantId, match.Matched[1]);
        Assert.Equal(catalog["A-2"].VariantId, match.Matched[2]);
        Assert.Empty(match.PartialMatch);
        Assert.Empty(match.InOtherProduct);
    }

    [Fact]
    public void SomeVariantsMatchingIsPartial()
    {
        var match = ShopifyProductMatcher.Match(Product(Variant(1, "A-1"), Variant(2, "NUEVA"), Variant(3, null)), Catalog(("A-1", ProductA)), NoImported);

        Assert.Equal(ShopifyMatchKind.Partial, match.Kind);
        Assert.Equal([1L], match.Matched.Keys);
        Assert.Equal([2L, 3L], match.PartialMatch);
    }

    [Fact]
    public void AVariantOfAnotherQepProductIsNeverLinked()
    {
        var match = ShopifyProductMatcher.Match(
            Product(Variant(1, "A-1"), Variant(2, "A-2"), Variant(3, "B-1")),
            Catalog(("A-1", ProductA), ("A-2", ProductA), ("B-1", ProductB)),
            NoImported);

        Assert.Equal((ShopifyMatchKind.Partial, (Guid?)ProductA), (match.Kind, match.QepProductId));
        Assert.Equal([1L, 2L], match.Matched.Keys.Order());
        Assert.Equal([3L], match.InOtherProduct);
        Assert.Empty(match.PartialMatch);
    }

    [Fact]
    public void TheProductWithMoreMatchesIsChosen()
    {
        var match = ShopifyProductMatcher.Match(
            Product(Variant(1, "A-1"), Variant(2, "B-1"), Variant(3, "B-2")),
            Catalog(("A-1", ProductA), ("B-1", ProductB), ("B-2", ProductB)),
            NoImported);

        Assert.Equal(ProductB, match.QepProductId);
        Assert.Equal([1L], match.InOtherProduct);
    }

    [Fact]
    public void ATieGoesToTheProductOfTheFirstMatchingVariant()
    {
        var match = ShopifyProductMatcher.Match(
            Product(Variant(1, "B-1"), Variant(2, "A-1")),
            Catalog(("A-1", ProductA), ("B-1", ProductB)),
            NoImported);

        Assert.Equal(ProductB, match.QepProductId);
    }

    [Fact]
    public void AMatchOnAnImportedProductOfTheConnectionIsRelink()
    {
        var match = ShopifyProductMatcher.Match(Product(Variant(1, "A-1")), Catalog(("A-1", ProductA)), new HashSet<Guid> { ProductA });

        Assert.Equal((ShopifyMatchKind.Relink, (Guid?)ProductA), (match.Kind, match.QepProductId));
        Assert.Single(match.Matched);
    }

    [Fact]
    public void VariantsWithoutSkuNeverMatchBySku()
    {
        var catalog = new Dictionary<string, ShopifyProductRef>(StringComparer.Ordinal)
        {
            [string.Empty] = new(Guid.CreateVersion7(), ProductA, string.Empty, true, 19),
            ["shp-1"] = new(Guid.CreateVersion7(), ProductA, "shp-1", true, 19),
        };

        var match = ShopifyProductMatcher.Match(Product(Variant(1, null), Variant(2, "  ")), catalog, NoImported);

        Assert.Equal(ShopifyMatchKind.None, match.Kind);
    }

    [Fact]
    public void TwoShopifyVariantsWithTheSameSkuLinkOnlyTheFirst()
    {
        var match = ShopifyProductMatcher.Match(Product(Variant(1, "A-1"), Variant(2, "A-1")), Catalog(("A-1", ProductA)), NoImported);

        Assert.Equal(ShopifyMatchKind.Partial, match.Kind);
        Assert.Equal([1L], match.Matched.Keys);
        Assert.Equal([2L], match.PartialMatch);
    }

    [Fact]
    public void TheSkusToLookUpAreTrimmedAndDistinct()
    {
        Assert.Equal(["A-1", "B"], ShopifyProductMatcher.SkusOf(Product(Variant(1, " A-1 "), Variant(2, "A-1"), Variant(3, null), Variant(4, "B"))));
    }
}
