using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Variantes, Tarea 3: el mapeo de variantes, galería y biblioteca de opciones, y la traducción de
/// los índices únicos de product_variants **por nombre**. El código de barras todavía no entra por
/// HTTP (es de la Tarea 5), así que su traducción se prueba en la unidad de trabajo.
/// </summary>
public sealed class CatalogVariantPersistenceTests
{
    private const string TenantId = "01900000-0000-7000-8000-00000000e001";
    private const string SubjectId = "01900000-0000-7000-8000-00000000e002";
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateWithTakenCodeReturns422CodeTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);

        var first = await CreateProductAsync(client, "Vela de soja", "VS-001");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await CreateProductAsync(client, "Otra vela", "VS-001");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("catalog.product.code_taken", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingATakenBarcodeThrowsBarcodeTaken()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.Parse(TenantId);

        await SaveAsync(factory, NewProduct(tenantId, "CAM-001", barcode: "7701234567890"));

        var error = await Assert.ThrowsAsync<CatalogDomainException>(
            () => SaveAsync(factory, NewProduct(tenantId, "CAM-002", barcode: "7701234567890")));

        Assert.Equal("catalog.variant.barcode_taken", error.Code);
    }

    [Fact]
    public async Task VariantsWithoutBarcodeDoNotCollide()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.Parse(TenantId);

        await SaveAsync(factory, NewProduct(tenantId, "CAM-001", barcode: null));
        await SaveAsync(factory, NewProduct(tenantId, "CAM-002", barcode: null));

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(2, await dbContext.ProductVariants.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AProductWithOptionsVariantsAndGalleryRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.Parse(TenantId);
        var talla = OptionDefinition.Create(tenantId, "Talla", ["M", "L"], isActive: true, Now);
        var front = Guid.CreateVersion7();
        var back = Guid.CreateVersion7();
        var product = NewProduct(tenantId, "CAM-001", barcode: null);
        var defaultVariantId = product.Variants.Single().Id.Value;
        product.Update(
            "Camiseta básica",
            ProductDetails.Empty,
            [talla.Id.Value],
            [
                Variant("CAM-M", [talla.Values[0].Id], id: defaultVariantId, barcode: "111", galleryFileId: back),
                Variant("CAM-L", [talla.Values[1].Id], packagingUnits: [6, 12]),
            ],
            [front, back],
            Now.AddMinutes(1));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            dbContext.OptionDefinitions.Add(talla);
            scope.ServiceProvider.GetRequiredService<IProductRepository>().Add(product);
            await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var loaded = await readScope.ServiceProvider.GetRequiredService<IProductRepository>()
            .FindAsync(tenantId, product.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal([talla.Id.Value], loaded.OptionIds);
        Assert.Equal([front, back], loaded.Gallery.Select(image => image.FileId));
        Assert.Equal(front, loaded.ImageFileId);
        Assert.Equal(["CAM-M", "CAM-L"], loaded.Variants.Select(variant => variant.Code));
        var medium = loaded.Variants[0];
        Assert.Equal(defaultVariantId, medium.Id.Value);
        Assert.Equal("111", medium.Barcode);
        Assert.Equal([talla.Values[0].Id], medium.OptionValueIds);
        Assert.Equal(loaded.Gallery[1].Id, medium.GalleryImageId);
        Assert.Equal(25m, medium.PriceIn("USD"));
        Assert.Single(medium.PriceScales);
        var large = loaded.Variants[1];
        Assert.Equal([talla.Values[1].Id], large.OptionValueIds);
        Assert.Equal([6, 12], large.PackagingUnits);
        Assert.Null(large.GalleryImageId);

        // Por la variante, no por el producto: el código de la talla L encuentra el producto.
        var byCode = await readScope.ServiceProvider.GetRequiredService<IProductRepository>()
            .FindVariantByCodeAsync(tenantId, "CAM-L", TestContext.Current.CancellationToken);
        Assert.Equal(product.Id, byCode?.Id);
        var byBarcode = await readScope.ServiceProvider.GetRequiredService<IProductRepository>()
            .FindVariantByBarcodeAsync(tenantId, "111", TestContext.Current.CancellationToken);
        Assert.Equal(product.Id, byBarcode?.Id);
        var byIds = await readScope.ServiceProvider.GetRequiredService<IProductRepository>()
            .FindVariantsByIdsAsync(tenantId, [large.Id], TestContext.Current.CancellationToken);
        Assert.Equal(product.Id, Assert.Single(byIds).Id);
    }

    [Fact]
    public async Task SavingAChangedOptionStampsItsUpdatedAt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var option = OptionDefinition.Create(Guid.Parse(TenantId), "Talla", ["M"], isActive: false, Now);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<CatalogDbContext>().OptionDefinitions.Add(option);
            await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var saved = await dbContext.OptionDefinitions
                .SingleAsync(TestContext.Current.CancellationToken);
            saved.SetActive(true);
            await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readScope = factory.Services.CreateAsyncScope();
        var reloaded = await readScope.ServiceProvider.GetRequiredService<CatalogDbContext>()
            .OptionDefinitions.Include(value => value.Values)
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.True(reloaded.IsActive);
        Assert.Equal("M", Assert.Single(reloaded.Values).Value);
        Assert.Equal(Now, reloaded.CreatedAt);
        Assert.True(reloaded.UpdatedAt > Now);
    }

    private static Product NewProduct(Guid tenantId, string code, string? barcode) =>
        Product.Create(
            ProductId.New(),
            tenantId,
            "Camiseta básica",
            ProductDetails.Empty,
            Variant(code, [], barcode: barcode),
            [],
            Now);

    private static ProductVariantInput Variant(
        string code,
        IReadOnlyList<Guid> values,
        Guid? id = null,
        string? barcode = null,
        Guid? galleryFileId = null,
        IReadOnlyCollection<int>? packagingUnits = null) =>
        new()
        {
            Id = id,
            Code = code,
            Barcode = barcode,
            OptionValueIds = values,
            GalleryFileId = galleryFileId,
            Pricing = new ProductPricing
            {
                Prices = new Dictionary<string, decimal> { ["USD"] = 25m },
                Scales = id is null
                    ? []
                    : [new PriceScaleInput(6, 48, 10m, PriceScaleRestriction.Multiple, 6, false)],
                PackagingUnits = packagingUnits ?? [],
            },
        };

    private static async Task SaveAsync(QepApiFactory factory, Product product)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IProductRepository>().Add(product);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> CreateProductAsync(HttpClient client, string name, string code) =>
        client.PostAsJsonAsync(
            $"/api/v1/tenants/{TenantId}/catalog/products",
            new { name, code, pricing = new { prices = new Dictionary<string, decimal> { ["USD"] = 10m } } },
            TestContext.Current.CancellationToken);

    private static HttpClient CreateClient(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        client.DefaultRequestHeaders.Add(
            "X-Permissions",
            string.Join(',', CatalogPermissions.ProductRead, CatalogPermissions.ProductManage));
        return client;
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private sealed class QepApiFactory(string connectionString)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, nunca heredado de appsettings.json. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
        }
    }
}
