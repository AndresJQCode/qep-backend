using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.UnitTests;

/// <summary>
/// Lo que el PUT de la consola no puede cambiar en un producto que administra Shopify (spec
/// 2026-11-17 §5.5, plan D7): nombre, opciones, alta y baja de variantes, código, barras y precios
/// de lista. El resto se sigue editando en QEP.
/// </summary>
public sealed class ExternallyManagedProductsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TallaId = Guid.CreateVersion7();
    private static readonly Guid TallaS = Guid.CreateVersion7();
    private static readonly Guid TallaM = Guid.CreateVersion7();

    private static Product NewProduct(string? externalSource) =>
        Product.Create(
            ProductId.New(),
            TenantId,
            "Camiseta",
            ProductDetails.Empty,
            [TallaId],
            [
                new ProductVariantInput
                {
                    Code = "CAM-S",
                    Barcode = "7700001",
                    OptionValueIds = [TallaS],
                    Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 50_000m } }
                },
                new ProductVariantInput
                {
                    Code = "CAM-M",
                    OptionValueIds = [TallaM],
                    Pricing = new ProductPricing { Prices = new Dictionary<string, decimal> { ["COP"] = 55_000m } }
                }
            ],
            [],
            Now,
            externalSource: externalSource);

    // El cuerpo que manda el formulario sin tocar nada: cada variante guardada con sus datos.
    private static ProductVariantRequest RowOf(ProductVariant variant) =>
        new(
            variant.Id.Value,
            variant.Code,
            variant.Barcode,
            variant.OptionValueIds,
            new ProductPricingRequest(
                variant.Prices.ToDictionary(price => price.Currency, price => price.Amount),
                Scales: null,
                PackagingUnits: null),
            GalleryFileId: null,
            IsActive: variant.IsActive);

    private static UpdateProductCommand CommandFor(
        Product product,
        string? name = null,
        IReadOnlyList<Guid>? optionIds = null,
        IReadOnlyList<ProductVariantRequest>? variants = null,
        IReadOnlyList<Guid>? deletedVariantIds = null,
        Guid? taxRateId = null,
        Guid? categoryId = null,
        string? description = null) =>
        new(
            TenantId,
            product.Id.Value,
            product.Version,
            name ?? product.Name,
            description,
            taxRateId,
            categoryId,
            optionIds ?? product.OptionIds,
            variants ?? product.Variants.Select(RowOf).ToArray(),
            Gallery: [],
            DeletedVariantIds: deletedVariantIds);

    private static CatalogDomainException Rejected(Product product, UpdateProductCommand command)
    {
        var error = Assert.Throws<CatalogDomainException>(() => ExternallyManagedProducts.EnsureAllowed(product, command));
        Assert.Equal("catalog.product.managed_externally", error.Code);
        return error;
    }

    [Fact]
    public void AnOwnProductAcceptsAnyChange()
    {
        var product = NewProduct(externalSource: null);
        var rows = product.Variants.Select(RowOf).ToArray();
        rows[0] = rows[0] with { Code = "OTRO", Barcode = null, IsActive = false };

        ExternallyManagedProducts.EnsureAllowed(
            product,
            CommandFor(
                product,
                name: "Otro nombre",
                optionIds: [],
                variants: [.. rows, new ProductVariantRequest(null, "NUEVA", null, [], null, null, null)],
                deletedVariantIds: [product.Variants[1].Id.Value]));
    }

    [Fact]
    public void RenamingAnImportedProductPointsAtName()
    {
        var product = NewProduct(Product.ShopifySource);

        var error = Rejected(product, CommandFor(product, name: "Camiseta nueva"));

        Assert.Equal(["Name"], error.FieldErrors.Keys);
    }

    // Recortado: el agregado guarda el nombre sin espacios de borde, así que «Camiseta » no es un cambio.
    [Fact]
    public void TheSameNameWithSpacesIsNotAChange()
    {
        var product = NewProduct(Product.ShopifySource);

        ExternallyManagedProducts.EnsureAllowed(product, CommandFor(product, name: "  Camiseta "));
    }

    [Fact]
    public void ChangingAPriceOfAnImportedVariantPointsAtTheRow()
    {
        var product = NewProduct(Product.ShopifySource);
        var rows = product.Variants.Select(RowOf).ToArray();
        rows[1] = rows[1] with
        {
            Pricing = new ProductPricingRequest(new Dictionary<string, decimal> { ["COP"] = 60_000m }, null, null)
        };

        var error = Rejected(product, CommandFor(product, variants: rows));

        Assert.Equal(["Variants[1].Pricing.Prices"], error.FieldErrors.Keys);
    }

    [Fact]
    public void ChangingCodeBarcodeOrActiveOfAnImportedVariantPointsAtEachField()
    {
        var product = NewProduct(Product.ShopifySource);
        var rows = product.Variants.Select(RowOf).ToArray();
        rows[0] = rows[0] with { Code = "CAM-S2", Barcode = null, IsActive = false };

        var error = Rejected(product, CommandFor(product, variants: rows));

        Assert.Equal(
            ["Variants[0].Barcode", "Variants[0].Code", "Variants[0].IsActive"],
            error.FieldErrors.Keys.Order(StringComparer.Ordinal));
    }

    // Un precio que no viene hereda el guardado (ProductVariantChanges): no es un cambio.
    [Fact]
    public void ARowWithoutPricingOrActiveInheritsAndIsAllowed()
    {
        var product = NewProduct(Product.ShopifySource);
        var rows = product.Variants
            .Select(variant => RowOf(variant) with { Pricing = null, IsActive = null })
            .ToArray();

        ExternallyManagedProducts.EnsureAllowed(product, CommandFor(product, variants: rows));
    }

    [Fact]
    public void ScalesCostsAndTaxRateStayEditable()
    {
        var product = NewProduct(Product.ShopifySource);
        var rows = product.Variants
            .Select(variant => RowOf(variant) with
            {
                Pricing = new ProductPricingRequest(
                    variant.Prices.ToDictionary(price => price.Currency, price => price.Amount),
                    [new PriceScaleRequest(10, 20, 5m, null, null, null)],
                    [12]),
                Costs = new Dictionary<string, decimal> { ["COP"] = 20_000m }
            })
            .ToArray();

        ExternallyManagedProducts.EnsureAllowed(
            product,
            CommandFor(
                product,
                variants: rows,
                taxRateId: Guid.CreateVersion7(),
                categoryId: Guid.CreateVersion7(),
                description: "Algodón peinado"));
    }

    [Fact]
    public void AddingOrDeletingAVariantIsRejected()
    {
        var product = NewProduct(Product.ShopifySource);
        var rows = product.Variants.Select(RowOf).ToArray();

        var added = Rejected(
            product,
            CommandFor(product, variants: [.. rows, new ProductVariantRequest(null, "CAM-L", null, [TallaM], null, null, null)]));
        Assert.Equal(["Variants"], added.FieldErrors.Keys);

        var deleted = Rejected(
            product,
            CommandFor(product, variants: [rows[0]], deletedVariantIds: [product.Variants[1].Id.Value]));
        Assert.Contains("DeletedVariantIds", deleted.FieldErrors.Keys);
    }

    [Fact]
    public void ChangingTheOptionsIsRejected()
    {
        var product = NewProduct(Product.ShopifySource);

        var error = Rejected(product, CommandFor(product, optionIds: []));

        Assert.Equal(["OptionIds"], error.FieldErrors.Keys);
    }
}
