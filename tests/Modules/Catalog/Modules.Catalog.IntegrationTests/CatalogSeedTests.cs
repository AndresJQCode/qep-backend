using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Infrastructure.Persistence;
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

        var products = await dbContext.Products.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(19, products.Count);
        // `.Value` explícito: Product.TaxRateId es TaxRateId? y taxRate.Id es TaxRateId, así que
        // sin esto la sobrecarga que elige el compilador compara por object y la aserción miente.
        Assert.All(products, product => Assert.Equal(taxRate.Id, product.TaxRateId!.Value));
        Assert.All(products, product => Assert.True(product.IsActive));

        var bronceador = products.Single(product => product.Code == "7416");
        Assert.Equal(35900m, bronceador.PriceBaseCop);
        Assert.Equal(9.97m, bronceador.PriceBaseUsd);
        // Ocho de los diecinueve nombres llevan tilde; si el recurso embebido se lee con la
        // codificación equivocada, esta es la aserción que lo detecta.
        Assert.Equal(
            "COMBO ROSADO RITUAL DE SEDUCCIÓN",
            products.Single(product => product.Code == "3001").Name);
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
            builder.UseSetting("Seed:Enabled", seedEnabled ? "true" : "false");
            builder.UseSetting("Seed:OwnerEmail", ownerEmail ?? string.Empty);
        }
    }
}
