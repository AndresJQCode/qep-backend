using System.Net;
using System.Net.Http.Json;
using Modules.Pos.Application;
using static Modules.Pos.IntegrationTests.PosApiHarness;

namespace Modules.Pos.IntegrationTests;

/// <summary>
/// El punto de venta por variante (spec 2026-10-20 §5), de punta a punta: el producto con opciones
/// se crea por la API de Catalog, como lo haría una persona, y la caja escanea, busca y vende por
/// variante. Lo que estas pruebas ven y las unitarias no: que el lookup real de Bootstrapper
/// resuelva código de barras, <c>Code</c>, precio y nombre visible contra Postgres, que la
/// búsqueda de Catalog encuentre por código de barras, y que la línea guarde las dos referencias.
///
/// «Camiseta» tiene la opción «Talla» con M y L. La primera variante del POST toma el id del
/// producto (Catalog, <c>Product.Create</c>), así que M lleva el id del producto y L no: las
/// aserciones sobre L son las que distinguen <c>VariantId</c> de <c>ProductId</c>.
/// </summary>
public sealed class PosVariantApiTests
{
    private const string BarcodeM = "7701234000011";
    private const string BarcodeL = "7701234000028";

    private static readonly string[] Sizes = ["M", "L"];

    // El lector teclea el código de barras de L: vuelve L, con su precio, su código y su nombre
    // visible, y no la primera variante ni el producto.
    [Fact]
    public async Task ByCodeScannerBarcodeReturnsVariant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var shirt = await CreateShirtInTwoSizesAsync(world);

        var product = await world.Admin.GetFromJsonAsync<PosProductResponse>(
            $"{world.Url}/products/by-code?code={BarcodeL}", TestContext.Current.CancellationToken);

        Assert.NotNull(product);
        Assert.Equal(
            (shirt.L, shirt.ProductId, "CAM-L", (string?)BarcodeL, "Camiseta – L", (decimal?)32_000m, 19, true),
            (product.VariantId, product.ProductId, product.Code, product.Barcode, product.Name, product.UnitPrice,
                product.TaxPercentage, product.Sellable));
        Assert.NotEqual(product.ProductId, product.VariantId);
    }

    // Sin código de barras que coincida, el escáner encuentra la variante por su Code (lo que
    // teclea el cajero), exacto y con mayúsculas.
    [Fact]
    public async Task ByCodeFallsBackToTheVariantCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var shirt = await CreateShirtInTwoSizesAsync(world);

        var byCode = await world.Admin.GetFromJsonAsync<PosProductResponse>(
            $"{world.Url}/products/by-code?code=CAM-L", TestContext.Current.CancellationToken);
        var lower = await world.Admin.GetAsync(
            $"{world.Url}/products/by-code?code=cam-l", TestContext.Current.CancellationToken);

        Assert.Equal((shirt.L, "Camiseta – L", (string?)BarcodeL), (byCode!.VariantId, byCode.Name, byCode.Barcode));
        Assert.Equal(HttpStatusCode.NotFound, lower.StatusCode);
        Assert.Contains(
            "pos.product.not_found",
            await lower.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // Enfoque de revisión 4, de punta a punta: el texto escaneado es el Code de la gorra y el código
    // de barras de la camiseta L. Gana el código de barras.
    [Fact]
    public async Task ByCodeBarcodeWinsOverOtherVariantCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var shirt = await CreateShirtInTwoSizesAsync(world);
        var cap = await CreateProductAsync(world.Tenant.Seeder, world.Tenant.TenantId, BarcodeL, "Gorra", 15_000m, null);

        var product = await world.Admin.GetFromJsonAsync<PosProductResponse>(
            $"{world.Url}/products/by-code?code={BarcodeL}", TestContext.Current.CancellationToken);

        Assert.Equal((shirt.L, "CAM-L", "Camiseta – L"), (product!.VariantId, product.Code, product.Name));
        Assert.NotEqual(cap, product.ProductId);
    }

    // La cuadrícula: productos con sus variantes vendibles. La camiseta sale una vez, con sus dos
    // tallas, y se encuentra también por el código de barras de una de ellas; el champú, sin
    // opciones, trae su única variante con el id del producto.
    [Fact]
    public async Task SearchReturnsProductsWithTheirSellableVariants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var shirt = await CreateShirtInTwoSizesAsync(world);

        var byName = await SearchAsync(world, "camis");
        var byBarcode = await SearchAsync(world, BarcodeL);
        var plain = await SearchAsync(world, "sham");

        var shirtCard = Assert.Single(byName.Items);
        Assert.Equal(1, byName.Total);
        Assert.Equal((shirt.ProductId, "Camiseta", true), (shirtCard.ProductId, shirtCard.Name, shirtCard.HasOptions));
        Assert.Equal(
            [(shirt.M, "CAM-M", "Camiseta – M", (decimal?)30_000m, true), (shirt.L, "CAM-L", "Camiseta – L", (decimal?)32_000m, true)],
            shirtCard.Variants.Select(variant => (variant.VariantId, variant.Code, variant.Name, variant.UnitPrice, variant.Sellable)));
        Assert.Equal(shirt.ProductId, Assert.Single(byBarcode.Items).ProductId);
        var shampoo = Assert.Single(plain.Items);
        Assert.False(shampoo.HasOptions);
        Assert.Equal((world.Shampoo, world.Shampoo), (Assert.Single(shampoo.Variants).VariantId, shampoo.ProductId));
    }

    // Una talla desactivada no se ofrece en la cuadrícula (variantes vendibles), pero el escáner la
    // sigue encontrando, marcada: el cajero escanearía un código existente y vería «no existe».
    [Fact]
    public async Task AnInactiveSizeIsLeftOutOfTheGridButTheScannerShowsItMarked()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var shirt = await CreateShirtInTwoSizesAsync(world);
        await ExecuteSqlAsync(
            database, "UPDATE catalog.product_variants SET is_active = false WHERE id = @id", ("id", shirt.L));

        var page = await SearchAsync(world, "camis");
        var scanned = await world.Admin.GetFromJsonAsync<PosProductResponse>(
            $"{world.Url}/products/by-code?code={BarcodeL}", TestContext.Current.CancellationToken);

        Assert.Equal([shirt.M], Assert.Single(page.Items).Variants.Select(variant => variant.VariantId));
        Assert.Equal((shirt.L, false, "Inactive"), (scanned!.VariantId, scanned.Sellable, scanned.UnsellableReason));
    }

    // El carrito junta por variante: M y L del mismo producto son dos líneas, en el preview y en la
    // venta, cada una con su variante, su precio y su código, y las dos con el mismo producto.
    [Fact]
    public async Task SaleLinesGroupedByVariantNotProduct()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var shirt = await CreateShirtInTwoSizesAsync(world);
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();

        var preview = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales/preview",
            new
            {
                lines = new[]
                {
                    new { variantId = shirt.M, quantity = 1m, discountPercentage = 0m },
                    new { variantId = shirt.L, quantity = 2m, discountPercentage = 0m },
                },
            },
            TestContext.Current.CancellationToken);
        var created = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(id, session.Id, ShirtLines(shirt), PosWorld.CashPayment(100_000m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewBody = await preview.Content.ReadFromJsonAsync<PosPreviewResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(
            [(shirt.M, (Guid?)shirt.ProductId, "Camiseta – M", (decimal?)30_000m), (shirt.L, shirt.ProductId, "Camiseta – L", 32_000m)],
            previewBody!.Lines.Select(line => (line.VariantId, line.ProductId, line.Name, line.UnitPrice)));
        Assert.Equal(94_000m, previewBody.Total);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sale = await created.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(
            [(1, shirt.M, shirt.ProductId, "CAM-M", 1m, 30_000m), (2, shirt.L, shirt.ProductId, "CAM-L", 2m, 32_000m)],
            sale!.Lines.Select(line => (line.Position, line.VariantId, line.ProductId, line.Code, line.Quantity, line.UnitPrice)));
        Assert.Equal(94_000m, sale.Total);
        Assert.Equal(2L, await CountAsync(
            database, "SELECT count(DISTINCT variant_id) FROM pos.sale_lines WHERE sale_id = @id", ("id", id)));
        Assert.Equal(1L, await CountAsync(
            database, "SELECT count(DISTINCT product_id) FROM pos.sale_lines WHERE sale_id = @id", ("id", id)));
    }

    // El ticket imprime el nombre visible de la variante, y es una foto: renombrar el producto
    // después cambia lo que muestra el escáner, no lo que dice la venta.
    [Fact]
    public async Task SaleTicketNameIsVisibleVariantName()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var shirt = await CreateShirtInTwoSizesAsync(world);
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();
        var created = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(id, session.Id, [LLine(shirt, 1m)], PosWorld.CashPayment(40_000m)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var sale = await created.Content.ReadFromJsonAsync<PosSaleResponse>(TestContext.Current.CancellationToken);

        await RenameShirtAsync(world, shirt, "Camiseta polo");

        var reread = await world.Admin.GetFromJsonAsync<PosSaleResponse>(
            $"{world.Url}/sales/{id}", TestContext.Current.CancellationToken);
        var scanned = await world.Admin.GetFromJsonAsync<PosProductResponse>(
            $"{world.Url}/products/by-code?code=CAM-L", TestContext.Current.CancellationToken);
        Assert.Equal("Camiseta – L", Assert.Single(sale!.Lines).Name);
        Assert.Equal(("Camiseta – L", "CAM-L"), (Assert.Single(reread!.Lines).Name, reread.Lines[0].Code));
        Assert.Equal("Camiseta polo – L", scanned!.Name);
    }

    // La huella usa la variante: el mismo id de venta con la otra talla, igual cantidad y precio
    // (otro carrito), no es una repetición sino un conflicto, y la venta original queda.
    [Fact]
    public async Task TheSameSaleIdWithAnotherSizeIsAConflict()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        var shirt = await CreateShirtInTwoSizesAsync(world);
        var session = await world.OpenSessionAsync(world.Admin);
        var id = Guid.CreateVersion7();
        (await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(id, session.Id, [LLine(shirt, 1m)], PosWorld.CashPayment(40_000m)),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var other = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(
                id,
                session.Id,
                [new { variantId = shirt.M, quantity = 1m, discountPercentage = 0m, expectedUnitPrice = 32_000m, expectedTaxPercentage = 19 }],
                PosWorld.CashPayment(40_000m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        Assert.Contains(
            "pos.sale.id_conflict",
            await other.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        var original = await world.Admin.GetFromJsonAsync<PosSaleResponse>(
            $"{world.Url}/sales/{id}", TestContext.Current.CancellationToken);
        Assert.Equal(shirt.L, Assert.Single(original!.Lines).VariantId);
    }

    // Un id que no es de ninguna variante del tenant responde el código de siempre: es contrato con
    // el frontend y no cambia con las variantes.
    [Fact]
    public async Task AnIdThatIsNoVariantIsProductNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var world = await PosWorld.ArrangeAsync(factory, database);
        await CreateShirtInTwoSizesAsync(world);
        var session = await world.OpenSessionAsync(world.Admin);

        var response = await world.Admin.PostAsJsonAsync(
            $"{world.Url}/sales",
            PosWorld.SaleBody(
                Guid.CreateVersion7(),
                session.Id,
                [new { variantId = Guid.CreateVersion7(), quantity = 1m, discountPercentage = 0m, expectedUnitPrice = 30_000m, expectedTaxPercentage = 19 }],
                PosWorld.CashPayment(40_000m)),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(
            "pos.sale.product_not_found",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // ---- Venta ----

    private static object[] ShirtLines(Shirt shirt) =>
    [
        new { variantId = shirt.M, quantity = 1m, discountPercentage = 0m, expectedUnitPrice = 30_000m, expectedTaxPercentage = 19 },
        LLine(shirt, 2m),
    ];

    private static object LLine(Shirt shirt, decimal quantity) =>
        new { variantId = shirt.L, quantity, discountPercentage = 0m, expectedUnitPrice = 32_000m, expectedTaxPercentage = 19 };

    private static async Task<PosPage<PosCatalogProductResponse>> SearchAsync(PosWorld world, string search)
    {
        var page = await world.Admin.GetFromJsonAsync<PosPage<PosCatalogProductResponse>>(
            $"{world.Url}/products?search={Uri.EscapeDataString(search)}", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return page;
    }

    // ---- Siembra por la API de Catalog ----

    private sealed record Shirt(Guid ProductId, Guid M, Guid L, Guid OptionId, Guid ValueM, Guid ValueL);

    private sealed record OptionValueDto(Guid Id, string Name);

    private sealed record OptionDto(Guid Id, IReadOnlyList<OptionValueDto> Values);

    private sealed record VariantDto(Guid Id, string Code);

    private sealed record ProductDto(Guid Id, long Version, IReadOnlyList<VariantDto> Variants);

    private static object Pricing(decimal cop) => new
    {
        prices = new Dictionary<string, decimal> { ["COP"] = cop },
        scales = new object[] { new { fromUnit = 1, toUnit = 999_999, discount = 0m, restriction = "multiple", multiple = 1 } },
        packagingUnits = Array.Empty<int>(),
    };

    private static object[] ShirtVariants(Guid? m = null, Guid? valueM = null, Guid? l = null, Guid? valueL = null) =>
    [
        new { id = m, code = "CAM-M", barcode = BarcodeM, optionValueIds = new[] { valueM }, pricing = Pricing(30_000m) },
        new { id = l, code = "CAM-L", barcode = BarcodeL, optionValueIds = new[] { valueL }, pricing = Pricing(32_000m) },
    ];

    /// <summary>«Camiseta» con IVA 19 y la opción «Talla» M y L: CAM-M a 30.000 y CAM-L a 32.000 COP,
    /// cada una con su código de barras.</summary>
    private static async Task<Shirt> CreateShirtInTwoSizesAsync(PosWorld world)
    {
        var seeder = world.Tenant.Seeder;
        var catalog = $"/api/v1/tenants/{world.Tenant.TenantId}/catalog";
        var optionResponse = await seeder.PostAsJsonAsync(
            $"{catalog}/options", new { name = "Talla", values = Sizes }, TestContext.Current.CancellationToken);
        Assert.True(
            optionResponse.IsSuccessStatusCode,
            await optionResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var option = await optionResponse.Content.ReadFromJsonAsync<OptionDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(option);
        var valueM = option.Values.Single(value => value.Name == "M").Id;
        var valueL = option.Values.Single(value => value.Name == "L").Id;

        var productResponse = await seeder.PostAsJsonAsync(
            $"{catalog}/products",
            new
            {
                name = "Camiseta",
                taxRateId = world.Tax19,
                optionIds = new[] { option.Id },
                variants = ShirtVariants(valueM: valueM, valueL: valueL),
            },
            TestContext.Current.CancellationToken);
        Assert.True(
            productResponse.IsSuccessStatusCode,
            await productResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var product = await productResponse.Content.ReadFromJsonAsync<ProductDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(product);

        return new Shirt(
            product.Id,
            product.Variants.Single(variant => variant.Code == "CAM-M").Id,
            product.Variants.Single(variant => variant.Code == "CAM-L").Id,
            option.Id,
            valueM,
            valueL);
    }

    /// <summary>Renombra el producto por el PUT de Catalog con las mismas variantes, códigos, códigos
    /// de barras, precios e IVA: lo único que cambia es el nombre, y con él el nombre visible.</summary>
    private static async Task RenameShirtAsync(PosWorld world, Shirt shirt, string name)
    {
        var url = $"/api/v1/tenants/{world.Tenant.TenantId}/catalog/products/{shirt.ProductId}";
        var current = await world.Tenant.Seeder.GetFromJsonAsync<ProductDto>(url, TestContext.Current.CancellationToken);
        Assert.NotNull(current);
        var response = await world.Tenant.Seeder.PutAsJsonAsync(
            url,
            new
            {
                expectedVersion = current.Version,
                name,
                taxRateId = world.Tax19,
                optionIds = new[] { shirt.OptionId },
                variants = ShirtVariants(shirt.M, shirt.ValueM, shirt.L, shirt.ValueL),
            },
            TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
