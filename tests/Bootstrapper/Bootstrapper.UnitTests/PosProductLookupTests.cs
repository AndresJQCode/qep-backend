using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Catalog → Pos por variante (spec 2026-10-20 §5). La portada que el POS le muestra al cajero: sólo la del tenant y sólo si
/// el archivo ya terminó su ciclo de subida (preflight F-16); una en cuarentena, pendiente o ajena sale como null. El escáner
/// busca primero por código de barras y después por <c>Code</c> (Enfoque de revisión 4).
/// </summary>
public sealed class PosProductLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAvailableImageOfTheTenantGivesItsUrl()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = LookupFor(fileId, new ProductImageRef(fileId, TenantId, "image/png", true, "https://cdn.example.co/a.png"), out var variantId);

        var found = await lookup.FindManyAsync(TenantId, [variantId], Ct);

        Assert.Equal("https://cdn.example.co/a.png", found[variantId].ImageUrl);
    }

    [Fact]
    public async Task AQuarantinedOrUnavailableImageGivesNoUrl()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = LookupFor(fileId, new ProductImageRef(fileId, TenantId, "image/png", false, "https://cdn.example.co/a.png"), out var variantId);

        var found = await lookup.FindManyAsync(TenantId, [variantId], Ct);

        Assert.Null(found[variantId].ImageUrl);
    }

    [Fact]
    public async Task AnImageOfAnotherTenantGivesNoUrl()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = LookupFor(fileId, new ProductImageRef(fileId, Guid.CreateVersion7(), "image/png", true, "https://cdn.example.co/a.png"), out var variantId);

        var found = await lookup.FindManyAsync(TenantId, [variantId], Ct);

        Assert.Null(found[variantId].ImageUrl);
    }

    [Fact]
    public async Task AProductWithoutImageGivesNoUrlAndNoImageLookup()
    {
        var product = NewProduct(imageFileId: null);
        var images = new FakeProductImageLookup();
        var lookup = Lookup(new FakeProductRepository(product), images: images);

        var found = await lookup.FindManyAsync(TenantId, [product.Id.Value], Ct);

        Assert.Null(found[product.Id.Value].ImageUrl);
        Assert.Equal(0, images.Calls);
    }

    // El POS elige la lista por la moneda de la caja; el adaptador entrega las dos. La variante por defecto lleva el id del
    // producto y su nombre.
    [Fact]
    public async Task AProductCarriesBothPriceLists()
    {
        var product = NewProduct(imageFileId: null, baseUsd: 3.25m);
        var lookup = Lookup(new FakeProductRepository(product));

        var found = await lookup.FindManyAsync(TenantId, [product.Id.Value], Ct);

        var variant = found[product.Id.Value];
        Assert.Equal(new Dictionary<string, decimal> { ["COP"] = 11_900m, ["USD"] = 3.25m }, variant.Prices);
        Assert.Equal(product.Id.Value, variant.ProductId);
        Assert.Equal("Shampoo 400 ml", variant.Name);
    }

    // Enfoque de revisión 4: «7702000000017» es el Code del shampoo y el código de barras de la camiseta L. Gana el código
    // de barras, y el Code ni se consulta.
    [Fact]
    public async Task PosLookupBarcodeBeatsCode()
    {
        var world = VariantWorld.Create(TenantId, Now, largeBarcode: "7702000000017");
        var shampoo = NewProduct(imageFileId: null, code: "7702000000017");
        var repository = new FakeProductRepository(shampoo, world.Product);
        var lookup = Lookup(repository, new FakeOptionDefinitionRepository(world.Size));

        var found = await lookup.FindByCodeAsync(TenantId, "7702000000017", Ct);

        Assert.NotNull(found);
        Assert.Equal(world.Large.Id.Value, found.VariantId);
        Assert.Equal(world.Product.Id.Value, found.ProductId);
        Assert.Equal("CAM-L", found.Code);
        Assert.Equal("7702000000017", found.Barcode);
        Assert.Equal("Camiseta básica – L", found.Name);
        Assert.Equal(32_000m, found.Prices["COP"]);
        Assert.Equal(0, repository.CodeLookups);
    }

    // Decisión del owner, 2026-10-21 (anti-objetivo §1: el escáner no deja de encontrar lo que hoy
    // encuentra). Si el código de barras cae en una variante inactiva y el mismo texto es el Code de una
    // variante activa, gana la activa: antes la caja devolvía la inactiva y nunca encontraba la otra.
    [Fact]
    public async Task PosLookupInactiveBarcodeYieldsToAnActiveCode()
    {
        var world = VariantWorld.Create(TenantId, Now, largeBarcode: "7702000000017", largeIsActive: false);
        var shampoo = NewProduct(imageFileId: null, code: "7702000000017");
        var lookup = Lookup(new FakeProductRepository(shampoo, world.Product), new FakeOptionDefinitionRepository(world.Size));

        var found = await lookup.FindByCodeAsync(TenantId, "7702000000017", Ct);

        Assert.NotNull(found);
        Assert.Equal((shampoo.Variants.Single().Id.Value, true), (found.VariantId, found.IsActive));
    }

    // Si las dos están inactivas, sigue ganando el código de barras: la caja la muestra marcada.
    [Fact]
    public async Task PosLookupInactiveBarcodeWinsWhenTheCodeIsInactiveToo()
    {
        var world = VariantWorld.Create(TenantId, Now, largeBarcode: "7702000000017", largeIsActive: false);
        var shampoo = NewProduct(imageFileId: null, code: "7702000000017");
        shampoo.Deactivate(Now);
        var lookup = Lookup(new FakeProductRepository(shampoo, world.Product), new FakeOptionDefinitionRepository(world.Size));

        var found = await lookup.FindByCodeAsync(TenantId, "7702000000017", Ct);

        Assert.NotNull(found);
        Assert.Equal((world.Large.Id.Value, false), (found.VariantId, found.IsActive));
    }

    [Fact]
    public async Task PosLookupFindsByCodeWhenNoBarcodeMatches()
    {
        var world = VariantWorld.Create(TenantId, Now, mediumBarcode: "7702000000024");
        var repository = new FakeProductRepository(world.Product);
        var lookup = Lookup(repository, new FakeOptionDefinitionRepository(world.Size));

        var found = await lookup.FindByCodeAsync(TenantId, "CAM-M", Ct);

        Assert.NotNull(found);
        Assert.Equal(world.Medium.Id.Value, found.VariantId);
        Assert.Equal("7702000000024", found.Barcode);
        Assert.Equal("Camiseta básica – M", found.Name);
        Assert.Equal(1, repository.BarcodeLookups);
        Assert.Equal(1, repository.CodeLookups);
    }

    // Igualdad exacta con mayúsculas, como el índice único: el lector manda el código tal cual.
    [Fact]
    public async Task PosLookupCodeIsCaseSensitiveAndUnknownIsNull()
    {
        var world = VariantWorld.Create(TenantId, Now);
        var lookup = Lookup(new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size));

        Assert.Null(await lookup.FindByCodeAsync(TenantId, "cam-m", Ct));
        Assert.Null(await lookup.FindByCodeAsync(TenantId, "NO-EXISTE", Ct));
    }

    // La caja muestra una variante inactiva marcada en vez de decir que no existe: vuelve con IsActive en false.
    [Fact]
    public async Task PosLookupInactiveVariantIsFoundButNotSellable()
    {
        var world = VariantWorld.Create(TenantId, Now, largeIsActive: false);
        var lookup = Lookup(new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size));

        var found = await lookup.FindByCodeAsync(TenantId, "CAM-L", Ct);

        Assert.NotNull(found);
        Assert.False(found.IsActive);
    }

    // La cuadrícula: productos, cada uno con sus variantes vendibles y su portada; las fotos de la página en una llamada.
    [Fact]
    public async Task PosSearchGroupsSellableVariantsByProduct()
    {
        var world = VariantWorld.Create(TenantId, Now, largeIsActive: false);
        var images = new FakeProductImageLookup(
            new ProductImageRef(world.MainFileId, TenantId, "image/png", true, "https://cdn.example.co/main.png"),
            new ProductImageRef(world.MediumFileId, TenantId, "image/png", true, "https://cdn.example.co/m.png"));
        var lookup = Lookup(new FakeProductRepository(world.Product), new FakeOptionDefinitionRepository(world.Size), images);

        var (items, total) = await lookup.SearchAsync(TenantId, null, null, 1, 20, Ct);

        Assert.Equal(1, total);
        var product = Assert.Single(items);
        Assert.Equal(world.Product.Id.Value, product.ProductId);
        Assert.Equal("Camiseta básica", product.Name);
        Assert.True(product.HasOptions);
        Assert.Equal("https://cdn.example.co/main.png", product.ImageUrl);
        var medium = Assert.Single(product.Variants);
        Assert.Equal(world.Medium.Id.Value, medium.VariantId);
        Assert.Equal("https://cdn.example.co/m.png", medium.ImageUrl);
        Assert.Equal(1, images.Calls);
    }

    // Spec 2026-10-26 §3.4: el filtro de la caja es la categoría y todas sus descendientes; el repositorio
    // de productos recibe los ids ya expandidos.
    [Fact]
    public async Task SearchWithACategoryPassesTheCategoryAndItsDescendants()
    {
        var bebidas = ProductCategory.Create(ProductCategoryId.New(), TenantId, "Bebidas", null, Now);
        var gaseosas = ProductCategory.Create(ProductCategoryId.New(), TenantId, "Gaseosas", bebidas.Id, Now);
        var products = new FakeProductRepository();
        var lookup = Lookup(products, categories: new FakeProductCategoryRepository((bebidas, 0), (gaseosas, 0)));

        await lookup.SearchAsync(TenantId, null, bebidas.Id.Value, 1, 40, Ct);

        Assert.NotNull(products.LastCategoryIds);
        Assert.Equal(
            new[] { bebidas.Id.Value, gaseosas.Id.Value }.Order(),
            products.LastCategoryIds.Order());
    }

    [Fact]
    public async Task SearchWithoutACategoryPassesNull()
    {
        var products = new FakeProductRepository();
        var lookup = Lookup(products);

        await lookup.SearchAsync(TenantId, null, null, 1, 40, Ct);

        Assert.Equal(1, products.SearchCalls);
        Assert.Null(products.LastCategoryIds);
    }

    // Un id que no existe o es de otro tenant da página vacía, no 404, y ni siquiera llega a la consulta de productos.
    [Fact]
    public async Task SearchWithAnUnknownCategoryIsEmptyWithoutQueryingProducts()
    {
        var products = new FakeProductRepository(NewProduct(imageFileId: null));
        var lookup = Lookup(products);

        var (items, total) = await lookup.SearchAsync(TenantId, null, Guid.CreateVersion7(), 1, 40, Ct);

        Assert.Empty(items);
        Assert.Equal(0, total);
        Assert.Equal(0, products.SearchCalls);
    }

    private static PosProductLookup Lookup(
        FakeProductRepository products,
        FakeOptionDefinitionRepository? options = null,
        FakeProductImageLookup? images = null,
        FakeProductCategoryRepository? categories = null) =>
        new(products, options ?? new FakeOptionDefinitionRepository(), new FakeTaxRateRepository(), images ?? new FakeProductImageLookup(),
            categories ?? new FakeProductCategoryRepository());

    private static PosProductLookup LookupFor(Guid fileId, ProductImageRef image, out Guid variantId)
    {
        var product = NewProduct(fileId);
        variantId = product.Id.Value;
        return Lookup(new FakeProductRepository(product), images: new FakeProductImageLookup(image));
    }

    private static Product NewProduct(Guid? imageFileId, decimal? baseUsd = null, string? code = null) =>
        Product.Create(
            ProductId.New(), TenantId, "Shampoo 400 ml",
            ProductDetails.Empty,
            new ProductVariantInput
            {
                Code = code ?? $"SH-{Guid.CreateVersion7():N}"[..12],
                Pricing = new ProductPricing
                {
                    Prices = baseUsd is { } usd
                    ? new Dictionary<string, decimal> { ["COP"] = 11_900m, ["USD"] = usd }
                    : new Dictionary<string, decimal> { ["COP"] = 11_900m }
                }
            }, imageFileId is { } image ? [image] : [],
            Now);
}
