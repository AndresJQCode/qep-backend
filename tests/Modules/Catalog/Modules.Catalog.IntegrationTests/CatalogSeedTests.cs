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
        Assert.Equal(35900m, bronceador.PriceIn("COP"));
        Assert.Equal(9.97m, bronceador.PriceIn("USD"));
        // Ocho de los diecinueve nombres llevan tilde; si el recurso embebido se lee con la
        // codificación equivocada, esta es la aserción que lo detecta.
        Assert.Equal(
            "COMBO ROSADO RITUAL DE SEDUCCIÓN",
            products.Single(product => product.Code == "3001").Name);

        // Finals are derived, never stored (spec D4): PriceScale.FinalFor over the product prices.
        // 9.97 × 0.85 = 8.4745 → 8.47; 35900 × 0.85 = 30515.
        var bronceadorFirst = bronceador.PriceScales.Single(scale => scale.FromUnit == 6);
        Assert.Equal(48, bronceadorFirst.ToUnit);
        Assert.Equal(15m, bronceadorFirst.Discount);
        Assert.Equal(PriceScaleRestriction.Multiple, bronceadorFirst.Restriction);
        Assert.Equal(3, bronceadorFirst.Multiple);
        Assert.True(bronceadorFirst.AllowGrouping);
        Assert.Equal(8.47m, PriceScale.FinalFor(bronceador.PriceIn("USD"), bronceadorFirst.Discount));
        Assert.Equal(30515m, PriceScale.FinalFor(bronceador.PriceIn("COP"), bronceadorFirst.Discount));

        // 9.97 × 0.65 = 6.4805 → 6.48; 35900 × 0.65 = 23335.
        var bronceadorLast = bronceador.PriceScales.Single(scale => scale.FromUnit == 1000);
        Assert.Equal(999999, bronceadorLast.ToUnit);
        Assert.Equal(35m, bronceadorLast.Discount);
        Assert.Equal(PriceScaleRestriction.PackagingUnit, bronceadorLast.Restriction);
        Assert.Null(bronceadorLast.Multiple);
        // El empaque es del producto desde el 2026-10-01, no de la escala.
        Assert.Equal([108], bronceador.PackagingUnits);
        Assert.False(bronceadorLast.AllowGrouping);
        Assert.Equal(6.48m, PriceScale.FinalFor(bronceador.PriceIn("USD"), bronceadorLast.Discount));
        Assert.Equal(23335m, PriceScale.FinalFor(bronceador.PriceIn("COP"), bronceadorLast.Discount));

        // KIT KERATINA tiene su propia columna de descuentos. 33.33 es el 0.6667 de la hoja:
        // 39.47 × 0.6667 = 26.3146… → 26.31; 150000 × 0.6667 = 100005.
        var keratina = products.Single(product => product.Code == "7701");
        var keratinaThird = keratina.PriceScales.Single(scale => scale.FromUnit == 100);
        Assert.Equal(299, keratinaThird.ToUnit);
        Assert.Equal(33.33m, keratinaThird.Discount);
        Assert.Equal(PriceScaleRestriction.PackagingUnit, keratinaThird.Restriction);
        Assert.Equal([25], keratina.PackagingUnits);
        Assert.Equal(26.31m, PriceScale.FinalFor(keratina.PriceIn("USD"), keratinaThird.Discount));
        Assert.Equal(100005m, PriceScale.FinalFor(keratina.PriceIn("COP"), keratinaThird.Discount));
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
