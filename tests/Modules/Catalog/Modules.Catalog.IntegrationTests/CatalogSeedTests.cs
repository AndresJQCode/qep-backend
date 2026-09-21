using Bootstrapper.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Tenancy.Infrastructure.Seed;
using Testcontainers.PostgreSql;

namespace Modules.Catalog.IntegrationTests;

public sealed class CatalogSeedTests
{
    [Fact]
    public async Task SeedCreatesTheTaxRateAndEveryProduct()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var taxRate = await dbContext.TaxRates.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("IVA 19%", taxRate.Name);
        Assert.Equal(19, taxRate.Percentage);

        // Include explícito: CatalogDbContext.PriceScales es internal y las escalas no se
        // cargan solas con el producto.
        var products = await dbContext.Products
            .Include(product => product.PriceScales)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(19, products.Count);
        // `.Value` explícito: Product.TaxRateId es TaxRateId? y taxRate.Id es TaxRateId, así que
        // sin esto la sobrecarga que elige el compilador compara por object y la aserción miente.
        Assert.All(products, product => Assert.Equal(taxRate.Id, product.TaxRateId!.Value));
        Assert.All(products, product => Assert.True(product.IsActive));
        Assert.All(products, product => Assert.Equal(5, product.PriceScales.Count));

        var bronceador = products.Single(product => product.Code == "7416");
        Assert.Equal(35900m, bronceador.PriceBaseCop);
        Assert.Equal(9.97m, bronceador.PriceBaseUsd);
        // Ocho de los diecinueve nombres llevan tilde; si el recurso embebido se lee con la
        // codificación equivocada, esta es la aserción que lo detecta.
        Assert.Equal(
            "COMBO ROSADO RITUAL DE SEDUCCIÓN",
            products.Single(product => product.Code == "3001").Name);

        // Los finales no vienen en el JSON: los calcula el seeder con PriceScale.FinalFor a
        // partir de los precios base. 9.97 × 0.85 = 8.4745 → 8.47; 35900 × 0.85 = 30515.
        var bronceadorFirst = bronceador.PriceScales.Single(scale => scale.FromUnit == 6);
        Assert.Equal(48, bronceadorFirst.ToUnit);
        Assert.Equal(15m, bronceadorFirst.Discount);
        Assert.Equal(PriceScaleRestriction.Multiple, bronceadorFirst.Restriction);
        Assert.Equal(3, bronceadorFirst.Multiple);
        Assert.Null(bronceadorFirst.PackagingUnit);
        Assert.True(bronceadorFirst.AllowGrouping);
        Assert.Equal(8.47m, bronceadorFirst.FinalUsd);
        Assert.Equal(30515m, bronceadorFirst.FinalCop);

        // 9.97 × 0.65 = 6.4805 → 6.48; 35900 × 0.65 = 23335.
        var bronceadorLast = bronceador.PriceScales.Single(scale => scale.FromUnit == 1000);
        Assert.Equal(999999, bronceadorLast.ToUnit);
        Assert.Equal(35m, bronceadorLast.Discount);
        Assert.Equal(PriceScaleRestriction.PackagingUnit, bronceadorLast.Restriction);
        Assert.Null(bronceadorLast.Multiple);
        Assert.Equal(108, bronceadorLast.PackagingUnit);
        Assert.False(bronceadorLast.AllowGrouping);
        Assert.Equal(6.48m, bronceadorLast.FinalUsd);
        Assert.Equal(23335m, bronceadorLast.FinalCop);

        // KIT KERATINA tiene su propia columna de descuentos. 33.33 es el 0.6667 de la hoja:
        // 39.47 × 0.6667 = 26.3146… → 26.31; 150000 × 0.6667 = 100005.
        var keratina = products.Single(product => product.Code == "7701");
        var keratinaThird = keratina.PriceScales.Single(scale => scale.FromUnit == 100);
        Assert.Equal(299, keratinaThird.ToUnit);
        Assert.Equal(33.33m, keratinaThird.Discount);
        Assert.Equal(PriceScaleRestriction.PackagingUnit, keratinaThird.Restriction);
        Assert.Equal(25, keratinaThird.PackagingUnit);
        Assert.Equal(26.31m, keratinaThird.FinalUsd);
        Assert.Equal(100005m, keratinaThird.FinalCop);
    }

    // La app reinicia sola en k8s, así que la semilla corre muchas veces sobre la misma base.
    // Se llama al orquestador de nuevo sobre la misma base en vez de levantar una segunda
    // factory: eso es exactamente lo que hace un reinicio de pod.
    [Fact]
    public async Task SeedingTwiceLeavesTheSameState()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await factory.Services.RunQepSeedAsync(TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();

        Assert.Equal(19, await catalog.Products.CountAsync(TestContext.Current.CancellationToken));
        // 19 productos × 5 escalas: la segunda corrida no duplica escalas porque el seeder es
        // create-only por código y no vuelve a tocar un producto que ya existe.
        Assert.Equal(
            95, await catalog.Set<PriceScale>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await catalog.TaxRates.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await tenancy.Memberships.CountAsync(
                membership => membership.TenantId == new TenantId(TenancySeeder.SeedTenantId),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await identity.Users.CountAsync(
                user => user.Email == "semilla@qcode.co", TestContext.Current.CancellationToken));
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

    private sealed class QepApiFactory(
        string connectionString,
        bool seedEnabled,
        string? ownerEmail = "semilla@qcode.co")
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
            // Fijado, nunca heredado: con "infobip" y sus claves ausentes el validador de
            // Notifications falla al arrancar y todas las pruebas del archivo mueren antes
            // de su aserción.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:Enabled", seedEnabled ? "true" : "false");
            builder.UseSetting("Seed:OwnerEmail", ownerEmail ?? string.Empty);
        }
    }
}
