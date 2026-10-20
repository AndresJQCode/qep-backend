using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

public sealed class ProductVariantTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static ProductPricing PricedIn(decimal usd, params PriceScaleInput[] scales) =>
        new() { Prices = new Dictionary<string, decimal> { ["USD"] = usd }, Scales = scales };

    private static ProductVariantInput Variant(
        string code,
        IReadOnlyList<Guid>? values = null,
        Guid? id = null,
        ProductPricing? pricing = null) =>
        new()
        {
            Id = id,
            Code = code,
            OptionValueIds = values ?? [],
            Pricing = pricing ?? PricedIn(10m),
        };

    private static Product NewProduct(ProductVariantInput? defaultVariant = null) =>
        Product.Create(
            ProductId.New(),
            TenantId,
            "Camiseta básica",
            ProductDetails.Empty,
            defaultVariant ?? Variant("CAM-001"),
            [],
            Now);

    [Fact]
    public void CreateBuildsDefaultVariantWithProductId()
    {
        var product = NewProduct(Variant(" CAM-001 ", pricing: PricedIn(25m)));

        var variant = Assert.Single(product.Variants);
        Assert.Equal(product.Id.Value, variant.Id.Value);
        Assert.Equal("CAM-001", variant.Code);
        Assert.Empty(variant.OptionValueIds);
        Assert.Equal(25m, variant.PriceIn("USD"));
        Assert.True(variant.IsActive);
        Assert.Equal(0, variant.Position);
        Assert.Empty(product.OptionIds);
    }

    [Fact]
    public void UpdateRejectsTwoVariantsWithSameCombination()
    {
        var product = NewProduct();
        var talla = Guid.CreateVersion7();
        var m = Guid.CreateVersion7();

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update(
                "Camiseta básica",
                ProductDetails.Empty,
                [talla],
                [
                    Variant("CAM-001", [m], id: product.Id.Value),
                    Variant("CAM-002", [m]),
                ],
                [],
                Now.AddMinutes(5)));

        Assert.Equal("catalog.variant.combination_duplicate", error.Code);
    }

    [Fact]
    public void UpdateRejectsMoreThan2048Variants()
    {
        var product = NewProduct();
        var talla = Guid.CreateVersion7();
        var variants = Enumerable.Range(0, Product.MaxVariants + 1)
            .Select(index => Variant(
                $"CAM-{index}",
                [Guid.CreateVersion7()],
                id: index == 0 ? product.Id.Value : null))
            .ToArray();

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update("Camiseta básica", ProductDetails.Empty, [talla], variants, [], Now.AddMinutes(5)));

        Assert.Equal(2048, Product.MaxVariants);
        Assert.Equal("catalog.variant.limit_exceeded", error.Code);
    }

    [Fact]
    public void UpdateRejectsMoreThanThreeOptions()
    {
        var product = NewProduct();
        var options = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();
        var values = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).ToArray();

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update(
                "Camiseta básica",
                ProductDetails.Empty,
                options,
                [Variant("CAM-001", values, id: product.Id.Value)],
                [],
                Now.AddMinutes(5)));

        Assert.Equal(3, Product.MaxOptions);
        Assert.Equal("catalog.product.option_limit", error.Code);
    }

    [Fact]
    public void UpdateRejectsMoreThan250GalleryImages()
    {
        var product = NewProduct();
        var gallery = Enumerable.Range(0, Product.MaxGalleryImages + 1)
            .Select(_ => Guid.CreateVersion7())
            .ToArray();

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update(
                "Camiseta básica",
                ProductDetails.Empty,
                [],
                [Variant("CAM-001", id: product.Id.Value)],
                gallery,
                Now.AddMinutes(5)));

        Assert.Equal(250, Product.MaxGalleryImages);
        Assert.Equal("catalog.product.gallery_limit", error.Code);
    }

    [Fact]
    public void UpdateRejectsRequestMissingASavedVariant()
    {
        var product = NewProduct();

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update(
                "Camiseta básica",
                ProductDetails.Empty,
                [],
                [Variant("CAM-NUEVA")],
                [],
                Now.AddMinutes(5)));

        Assert.Equal("catalog.variant.missing_in_request", error.Code);
    }

    [Fact]
    public void UpdateRejectsAVariantWhoseValueCountDoesNotMatchTheOptions()
    {
        var product = NewProduct();

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update(
                "Camiseta básica",
                ProductDetails.Empty,
                [Guid.CreateVersion7(), Guid.CreateVersion7()],
                [Variant("CAM-001", [Guid.CreateVersion7()], id: product.Id.Value)],
                [],
                Now.AddMinutes(5)));

        Assert.Equal("catalog.variant.value_count_mismatch", error.Code);
    }

    // Cada variante se comporta como hoy un producto: sus escalas son suyas, con su propia FK, y
    // no se mezclan con las de otra variante del mismo producto.
    [Fact]
    public void PriceScaleCountsPerVariant()
    {
        var product = NewProduct();
        var talla = Guid.CreateVersion7();
        var m = Guid.CreateVersion7();
        var l = Guid.CreateVersion7();

        product.Update(
            "Camiseta básica",
            ProductDetails.Empty,
            [talla],
            [
                Variant("CAM-M", [m], id: product.Id.Value, pricing: PricedIn(
                    10m,
                    new PriceScaleInput(6, 12, 5m, PriceScaleRestriction.Multiple, 6))),
                Variant("CAM-L", [l], pricing: PricedIn(
                    12m,
                    new PriceScaleInput(1, 5, 0m, PriceScaleRestriction.Multiple, 1),
                    new PriceScaleInput(6, 12, 8m, PriceScaleRestriction.Multiple, 6))),
            ],
            [],
            Now.AddMinutes(5));

        var medium = product.Variants[0];
        var large = product.Variants[1];
        Assert.Equal(product.Id.Value, medium.Id.Value);
        var mediumScale = Assert.Single(medium.PriceScales);
        Assert.Equal(medium.Id, mediumScale.VariantId);
        Assert.Equal(2, large.PriceScales.Count);
        Assert.All(large.PriceScales, scale => Assert.Equal(large.Id, scale.VariantId));
        Assert.Equal([m], medium.OptionValueIds);
        Assert.Equal([l], large.OptionValueIds);
        Assert.Equal([talla], product.OptionIds);
        Assert.Equal(1, large.Position);
    }

    [Fact]
    public void UpdateKeepsTheSavedVariantAndItsPriceRows()
    {
        var product = NewProduct();
        var before = Assert.Single(product.Variants);
        var priceRow = Assert.Single(before.Prices);

        product.Update(
            "Camiseta básica",
            ProductDetails.Empty,
            [],
            [Variant("CAM-001B", id: product.Id.Value, pricing: PricedIn(11m))],
            [],
            Now.AddMinutes(5));

        var after = Assert.Single(product.Variants);
        Assert.Same(before, after);
        Assert.Same(priceRow, Assert.Single(after.Prices));
        Assert.Equal("CAM-001B", after.Code);
        Assert.Equal(11m, after.PriceIn("USD"));
        Assert.Equal(2, product.Version);
    }

    [Fact]
    public void UpdateLinksTheVariantImageToItsGalleryPhoto()
    {
        var product = NewProduct();
        var cover = Guid.CreateVersion7();
        var blue = Guid.CreateVersion7();

        product.Update(
            "Camiseta básica",
            ProductDetails.Empty,
            [],
            [Variant("CAM-001", id: product.Id.Value) with { GalleryFileId = blue }],
            [cover, blue],
            Now.AddMinutes(5));

        Assert.Equal([cover, blue], product.Gallery.Select(image => image.FileId));
        Assert.Equal([0, 1], product.Gallery.Select(image => image.Position));
        Assert.Equal(cover, product.ImageFileId);
        Assert.Equal(product.Gallery[1].Id, Assert.Single(product.Variants).GalleryImageId);
    }

    [Fact]
    public void UpdateRejectsAVariantImageOutsideTheGallery()
    {
        var product = NewProduct();

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update(
                "Camiseta básica",
                ProductDetails.Empty,
                [],
                [Variant("CAM-001", id: product.Id.Value) with { GalleryFileId = Guid.CreateVersion7() }],
                [Guid.CreateVersion7()],
                Now.AddMinutes(5)));

        Assert.Equal("catalog.variant.image_not_in_gallery", error.Code);
    }

    [Fact]
    public void CreateRejectsABarcodeOverSixtyFourCharacters()
    {
        var error = Assert.Throws<CatalogDomainException>(() =>
            NewProduct(Variant("CAM-001") with { Barcode = new string('7', ProductVariant.BarcodeMaxLength + 1) }));

        Assert.Equal(64, ProductVariant.BarcodeMaxLength);
        Assert.Equal("catalog.variant.barcode_too_long", error.Code);
    }

    [Fact]
    public void CreateTurnsABlankBarcodeIntoNoBarcode()
    {
        var product = NewProduct(Variant("CAM-001") with { Barcode = "   " });

        Assert.Null(Assert.Single(product.Variants).Barcode);
    }

    [Fact]
    public void UpdateRejectsTwoVariantsWithTheSameCode()
    {
        var product = NewProduct();
        var talla = Guid.CreateVersion7();

        var error = Assert.Throws<CatalogDomainException>(() =>
            product.Update(
                "Camiseta básica",
                ProductDetails.Empty,
                [talla],
                [
                    Variant("CAM-001", [Guid.CreateVersion7()], id: product.Id.Value),
                    Variant(" CAM-001 ", [Guid.CreateVersion7()]),
                ],
                [],
                Now.AddMinutes(5)));

        Assert.Equal("catalog.product.code_taken", error.Code);
    }

    // Una combinación repetida sólo choca entre variantes activas: quitar una opción puede dejar
    // dos con la misma combinación, y la que sobra se desactiva (spec §3.3.1).
    [Fact]
    public void UpdateAcceptsARepeatedCombinationWhenOneIsInactive()
    {
        var product = NewProduct();
        var talla = Guid.CreateVersion7();
        var m = Guid.CreateVersion7();

        product.Update(
            "Camiseta básica",
            ProductDetails.Empty,
            [talla],
            [
                Variant("CAM-001", [m], id: product.Id.Value),
                Variant("CAM-002", [m]) with { IsActive = false },
            ],
            [],
            Now.AddMinutes(5));

        Assert.Equal(2, product.Variants.Count);
        Assert.False(product.Variants[1].IsActive);
    }

    // Los accesos transitorios de Product (Code, precios, escalas, empaques) sólo valen para un
    // producto de una variante: con más, un llamador sin migrar leería una sola en silencio.
    [Fact]
    public void TransitionalAccessorsFailLoudlyWithMoreThanOneVariant()
    {
        var product = NewProduct();
        var talla = Guid.CreateVersion7();
        product.Update(
            "Camiseta básica",
            ProductDetails.Empty,
            [talla],
            [
                Variant("CAM-M", [Guid.CreateVersion7()], id: product.Id.Value),
                Variant("CAM-L", [Guid.CreateVersion7()]),
            ],
            [],
            Now.AddMinutes(5));

        var error = Assert.Throws<InvalidOperationException>(() => product.Code);
        Assert.Contains("transitorio", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => product.Prices);
        Assert.Throws<InvalidOperationException>(() => product.PricedCurrencies);
        Assert.Throws<InvalidOperationException>(() => product.PriceIn("USD"));
        Assert.Throws<InvalidOperationException>(() => product.PriceScales);
        Assert.Throws<InvalidOperationException>(() => product.PackagingUnits);
    }

    [Fact]
    public void TransitionalAccessorsReadTheOnlyVariant()
    {
        var product = NewProduct(Variant("CAM-001", pricing: PricedIn(25m)));

        Assert.Equal("CAM-001", product.Code);
        Assert.Equal(25m, product.PriceIn("USD"));
    }

    [Fact]
    public void CreateRejectsMoreThan250GalleryImages()
    {
        var gallery = Enumerable.Range(0, Product.MaxGalleryImages + 1)
            .Select(_ => Guid.CreateVersion7())
            .ToArray();

        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(ProductId.New(), TenantId, "Camiseta básica", ProductDetails.Empty, Variant("CAM-001"), gallery, Now));

        Assert.Equal("catalog.product.gallery_limit", error.Code);
    }

    [Fact]
    public void CreateRejectsARepeatedGalleryImage()
    {
        var photo = Guid.CreateVersion7();

        var error = Assert.Throws<CatalogDomainException>(() =>
            Product.Create(
                ProductId.New(), TenantId, "Camiseta básica", ProductDetails.Empty, Variant("CAM-001"),
                [photo, Guid.CreateVersion7(), photo], Now));

        Assert.Equal("catalog.product.gallery_duplicate", error.Code);
    }
}
