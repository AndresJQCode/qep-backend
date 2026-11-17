using System.Collections.Concurrent;
using BuildingBlocks.Domain.Taxes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Npgsql;
using static Modules.Catalog.IntegrationTests.CatalogTestDatabase;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// El camino de sistema de Catalog (spec 2026-11-17 §5.4, plan Tarea 3): lo que la importación y los
/// webhooks de Shopify escriben sin miembro ni <c>IExecutionContext</c>. Un host propio por clase:
/// la prueba escribe con el contenedor de servicios y reemplaza el verificador de uso de variantes.
/// Cada prueba usa un tenant propio.
/// </summary>
public sealed class ExternalProductWriterTests(ExternalProductWriterTests.Host host)
    : IClassFixture<ExternalProductWriterTests.Host>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private readonly Guid _tenantId = Guid.CreateVersion7();
    private readonly Guid _actorId = Guid.CreateVersion7();

    [Fact]
    public async Task CreatesAProductWithOptionsFoundByNameIgnoringCase()
    {
        var talla = await SeedOptionAsync("Talla", ["S", "M"], isActive: false);

        var result = await WriteAsync(writer => writer.CreateAsync(
            Draft("Camiseta", [new ExternalOptionDraft(" talla ", ["S", "M"])], Variant("1", "CAM-S", "S"), Variant("2", "CAM-M", "M")),
            TestContext.Current.CancellationToken));

        var product = await ReadAsync(result.ProductId);
        Assert.Equal([talla], product.OptionIds);
        Assert.Equal(Product.ShopifySource, product.ExternalSource);
        Assert.Equal(2, result.VariantIdsByKey.Count);
        Assert.True(result.Changed);

        var library = await ListOptionsAsync();
        var option = Assert.Single(library, candidate => candidate.Name.Equals("Talla", StringComparison.OrdinalIgnoreCase));
        Assert.True(option.IsActive);
    }

    [Fact]
    public async Task CreatesMissingOptionValues()
    {
        await SeedOptionAsync("Color", ["Azul"], isActive: true);

        var result = await WriteAsync(writer => writer.CreateAsync(
            Draft("Gorra", [new ExternalOptionDraft("Color", ["Rojo"])], Variant("1", "GOR-R", "Rojo")),
            TestContext.Current.CancellationToken));

        var color = Assert.Single(await ListOptionsAsync(), option => option.Name == "Color");
        var rojo = Assert.Single(color.Values, value => value.Value == "Rojo");
        Assert.True(rojo.IsActive);
        var product = await ReadAsync(result.ProductId);
        Assert.Equal([rojo.Id], product.Variants.Single().OptionValueIds);
    }

    [Fact]
    public async Task ABarcodeTakenInTheTenantIsDroppedAndReported()
    {
        await WriteAsync(writer => writer.CreateAsync(
            Draft("Vela", [], Variant("9", "VEL-1", barcode: "7701")),
            TestContext.Current.CancellationToken));

        var result = await WriteAsync(writer => writer.CreateAsync(
            Draft(
                "Jabón",
                [new ExternalOptionDraft("Aroma", ["Lavanda", "Coco"])],
                Variant("1", "JAB-L", "Lavanda", barcode: "7702"),
                Variant("2", "JAB-C", "Coco", barcode: "7701")),
            TestContext.Current.CancellationToken));

        Assert.Equal(["2"], result.DroppedBarcodeKeys);
        var product = await ReadAsync(result.ProductId);
        Assert.Null(product.Variants.Single(variant => variant.Id.Value == result.VariantIdsByKey["2"]).Barcode);
        Assert.Equal("7702", product.Variants.Single(variant => variant.Id.Value == result.VariantIdsByKey["1"]).Barcode);
    }

    [Fact]
    public async Task AnUpdateKeepsScalesPackagingCostsAndOtherCurrencies()
    {
        var draft = Draft("Vela", [], Variant("1", "VEL-1", price: 10_000m));
        var created = await WriteAsync(writer => writer.CreateAsync(draft, TestContext.Current.CancellationToken));
        // Lo que el asesor le agrega en QEP: escalas, empaques, costos y un precio en dólares.
        await MutateAsync(created.ProductId, product =>
        {
            var variant = product.Variants.Single();
            product.Update(
                product.Name,
                ProductDetails.Empty,
                [],
                [
                    new ProductVariantInput
                    {
                        Id = variant.Id.Value,
                        Code = variant.Code,
                        Pricing = new ProductPricing
                        {
                            Prices = new Dictionary<string, decimal> { ["COP"] = 10_000m, ["USD"] = 3m },
                            Scales = [new PriceScaleInput(1, 9, 10m, PriceScaleRestriction.Multiple, 1)],
                            PackagingUnits = [12]
                        },
                        Costs = new Dictionary<string, decimal> { ["COP"] = 4_000m }
                    }
                ],
                [],
                Now.AddMinutes(1));
        });

        await WriteAsync(writer => writer.UpdateAsync(
            created.ProductId,
            Draft("Vela", [], Variant("1", "VEL-1", price: 12_000m)),
            created.VariantIdsByKey,
            TestContext.Current.CancellationToken));

        var updated = (await ReadAsync(created.ProductId)).Variants.Single();
        Assert.Equal(12_000m, updated.Prices.Single(price => price.Currency == "COP").Amount);
        Assert.Equal(3m, updated.Prices.Single(price => price.Currency == "USD").Amount);
        Assert.Single(updated.PriceScales);
        Assert.Equal([12], updated.PackagingUnits);
        Assert.Equal(4_000m, updated.Costs.Single().Amount);
    }

    [Fact]
    public async Task AnUpdateWithoutChangesDoesNotBumpTheVersion()
    {
        var draft = Draft("Camiseta", [new ExternalOptionDraft("Talla", ["S", "M"])], Variant("1", "CAM-S", "S"), Variant("2", "CAM-M", "M"));
        var created = await WriteAsync(writer => writer.CreateAsync(draft, TestContext.Current.CancellationToken));

        var result = await WriteAsync(writer => writer.UpdateAsync(
            created.ProductId, draft, created.VariantIdsByKey, TestContext.Current.CancellationToken));

        Assert.False(result.Changed);
        Assert.Equal(created.Version, result.Version);
        Assert.Equal(created.Version, (await ReadAsync(created.ProductId)).Version);
    }

    [Fact]
    public async Task AnUpdateOfAnInactiveProductLeavesItInactive()
    {
        var created = await WriteAsync(writer => writer.CreateAsync(
            Draft("Vela", [], Variant("1", "VEL-1")) with { IsActive = false },
            TestContext.Current.CancellationToken));
        Assert.False((await ReadAsync(created.ProductId)).IsActive);

        var result = await WriteAsync(writer => writer.UpdateAsync(
            created.ProductId,
            Draft("Vela grande", [], Variant("1", "VEL-1")) with { IsActive = false },
            created.VariantIdsByKey,
            TestContext.Current.CancellationToken));

        var product = await ReadAsync(result.ProductId);
        Assert.True(result.Changed);
        Assert.False(product.IsActive);
        Assert.Equal("Vela grande", product.Name);
    }

    [Fact]
    public async Task AVariantMissingFromTheDraftIsDeletedOrDeactivatedByUsage()
    {
        var created = await WriteAsync(writer => writer.CreateAsync(
            Draft(
                "Camiseta",
                [new ExternalOptionDraft("Talla", ["S", "M", "L"])],
                Variant("1", "CAM-S", "S"),
                Variant("2", "CAM-M", "M"),
                Variant("3", "CAM-L", "L")),
            TestContext.Current.CancellationToken));
        var used = created.VariantIdsByKey["2"];
        var unused = created.VariantIdsByKey["3"];
        host.Usage.MarkUsed(used);

        await WriteAsync(writer => writer.UpdateAsync(
            created.ProductId,
            Draft("Camiseta", [new ExternalOptionDraft("Talla", ["S", "M", "L"])], Variant("1", "CAM-S", "S")),
            created.VariantIdsByKey,
            TestContext.Current.CancellationToken));

        var product = await ReadAsync(created.ProductId);
        Assert.DoesNotContain(product.Variants, variant => variant.Id.Value == unused);
        Assert.False(product.Variants.Single(variant => variant.Id.Value == used).IsActive);
        Assert.True(product.Variants.Single(variant => variant.Id.Value == created.VariantIdsByKey["1"]).IsActive);
    }

    // Un archivo que no entró a la galería quedaría Available sin dueño para siempre: se libera.
    [Fact]
    public async Task ReplacingAnImageThatDidNotEnterReleasesTheNewFile()
    {
        var created = await WriteAsync(writer => writer.CreateAsync(
            Draft("Vela", [], Variant("1", "VEL-1")), TestContext.Current.CancellationToken));
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        Assert.True(await WriteAsync(writer => writer.ReplaceImageAsync(
            _tenantId, created.ProductId, null, first, _actorId, Now, TestContext.Current.CancellationToken)));
        // previous no está en la galería (el asesor la quitó o es otra): la nueva no entra.
        Assert.False(await WriteAsync(writer => writer.ReplaceImageAsync(
            _tenantId, created.ProductId, Guid.CreateVersion7(), second, _actorId, Now, TestContext.Current.CancellationToken)));

        Assert.Equal([first], (await ReadAsync(created.ProductId)).Gallery.Select(image => image.FileId));
        var released = await ReleasedPayloadsAsync();
        Assert.Contains(released, payload => payload.Contains(second.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(released, payload => payload.Contains(first.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReleasingClearsTheSource()
    {
        var created = await WriteAsync(writer => writer.CreateAsync(
            Draft("Vela", [], Variant("1", "VEL-1")), TestContext.Current.CancellationToken));

        await WriteAsync(async writer =>
        {
            await writer.ReleaseAsync(_tenantId, created.ProductId, _actorId, Now, TestContext.Current.CancellationToken);
            return true;
        });

        Assert.Null((await ReadAsync(created.ProductId)).ExternalSource);
    }

    [Fact]
    public async Task DefaultRatesPickTheOldestActive19AndExempt()
    {
        var inactiveOldest = await SeedTaxRateAsync("IVA viejo", 19m, Now.AddDays(-30), deactivate: true);
        var oldest19 = await SeedTaxRateAsync("IVA general", 19m, Now.AddDays(-20));
        await SeedTaxRateAsync("IVA general 2", 19m, Now.AddDays(-10));
        var exempt = await SeedTaxRateAsync("Exento", 0m, Now.AddDays(-5));
        await SeedTaxRateAsync("IVA reducido", 5m, Now.AddDays(-25));

        var rates = await WriteAsync(writer => writer.FindDefaultTaxRatesAsync(_tenantId, TestContext.Current.CancellationToken));

        Assert.NotEqual(inactiveOldest, rates.Vat19Id);
        Assert.Equal(oldest19, rates.Vat19Id);
        Assert.Equal(exempt, rates.ExemptId);
    }

    [Fact]
    public async Task DefaultRatesAreNullWithoutRates()
    {
        var rates = await WriteAsync(writer => writer.FindDefaultTaxRatesAsync(_tenantId, TestContext.Current.CancellationToken));

        Assert.Null(rates.Vat19Id);
        Assert.Null(rates.ExemptId);
    }

    private ExternalProductDraft Draft(string name, IReadOnlyList<ExternalOptionDraft> options, params ExternalVariantDraft[] variants) =>
        new(_tenantId, Product.ShopifySource, name, IsActive: true, TaxRateId: null, Currency: "COP", options, variants, _actorId, Now);

    private static ExternalVariantDraft Variant(string key, string code, string? value = null, string? barcode = null, decimal price = 50_000m) =>
        new(key, code, barcode, value is null ? [] : [value], price);

    private async Task<T> WriteAsync<T>(Func<IExternalProductWriter, Task<T>> action)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IExternalProductWriter>());
    }

    private async Task<Product> ReadAsync(Guid productId)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var product = await scope.ServiceProvider.GetRequiredService<IProductRepository>()
            .FindAsync(_tenantId, new ProductId(productId), TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        return product;
    }

    private async Task MutateAsync(Guid productId, Action<Product> mutate)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var product = await scope.ServiceProvider.GetRequiredService<IProductRepository>()
            .FindAsync(_tenantId, new ProductId(productId), TestContext.Current.CancellationToken);
        Assert.NotNull(product);
        mutate(product);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedOptionAsync(string name, IReadOnlyList<string> values, bool isActive)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var option = OptionDefinition.Create(_tenantId, name, values, isActive, Now);
        scope.ServiceProvider.GetRequiredService<IOptionDefinitionRepository>().Add(option);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return option.Id.Value;
    }

    private async Task<IReadOnlyList<OptionDefinition>> ListOptionsAsync()
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOptionDefinitionRepository>()
            .ListAsync(_tenantId, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedTaxRateAsync(string name, decimal percentage, DateTimeOffset createdAt, bool deactivate = false)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var rate = TaxRate.Create(TaxRateId.New(), _tenantId, name, percentage, TaxCountries.IvaType, "CO", null, null, createdAt);
        if (deactivate)
        {
            rate.Deactivate(createdAt.AddMinutes(1));
        }

        scope.ServiceProvider.GetRequiredService<ITaxRateRepository>().Add(rate);
        await scope.ServiceProvider.GetRequiredService<ICatalogUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return rate.Id.Value;
    }

    private async Task<IReadOnlyList<string>> ReleasedPayloadsAsync()
    {
        await using var connection = new NpgsqlConnection(host.Database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT payload::text FROM platform.outbox_messages WHERE event_name = 'catalog.product.images-released.v1'",
            connection);
        var payloads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            payloads.Add(reader.GetString(0));
        }

        return payloads;
    }

    /// <summary>El verificador de uso de variantes de esta clase: una variante está en uso si una prueba
    /// la marcó. Sin cotizaciones ni ventas reales que sembrar.</summary>
    public sealed class ScriptedUsage
    {
        private readonly ConcurrentDictionary<Guid, bool> _used = new();

        public void MarkUsed(Guid variantId) => _used[variantId] = true;

        public IReadOnlySet<Guid> Answer(IReadOnlyCollection<Guid> variantIds) =>
            variantIds.Where(_used.ContainsKey).ToHashSet();
    }

    private sealed class ScriptedUsageChecker(ScriptedUsage usage) : IVariantUsageChecker
    {
        public Task<IReadOnlySet<Guid>> FindUsedAsync(
            Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
            Task.FromResult(usage.Answer(variantIds));
    }

    public sealed class Host : IAsyncLifetime
    {
        private TestDatabase? _database;
        private QepApiFactory? _factory;

        public ScriptedUsage Usage { get; } = new();

        internal TestDatabase Database => _database ?? throw new InvalidOperationException("Not initialized.");

        internal WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Not initialized.");

        public async ValueTask InitializeAsync()
        {
            _database = await StartDatabaseAsync();
            _factory = new QepApiFactory(_database.GetConnectionString(), Usage);
            // Fuerza el arranque del host, que aplica las migraciones.
            using var client = _factory.CreateClient();
        }

        public async ValueTask DisposeAsync()
        {
            if (_factory is not null)
            {
                await _factory.DisposeAsync();
            }

            if (_database is not null)
            {
                await _database.DisposeAsync();
            }
        }
    }

    private sealed class QepApiFactory(string connectionString, ScriptedUsage usage) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            // Fijado, nunca heredado: los user-secrets del developer pueden prender la semilla.
            builder.UseSetting("Seed:Enabled", "false");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.ConfigureTestServices(services =>
                services.AddScoped<IVariantUsageChecker>(_ => new ScriptedUsageChecker(usage)));
        }
    }
}
