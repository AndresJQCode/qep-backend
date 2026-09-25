using Bootstrapper.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Tenancy.Infrastructure.Seed;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La mitad de Quotations de la semilla de arranque: el tenant sembrado numera sus pedidos con la
/// serie del cliente, <c>PW234235</c> — el caso canónico del README (§ Numeración de documentos por
/// tenant). Se prueba contra la fila y no contra un pedido convertido: que la fila produzca ese número
/// ya lo cubren <c>DocumentNumberingApiTests</c>.
/// </summary>
public sealed class QuotationsSeedTests
{
    private const string OwnerEmail = "semilla@qcode.co";

    [Fact]
    public async Task SeedConfiguresTheOrderNumberingOfTheSeedTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = WithSeed(baseFactory);
        using var client = factory.CreateClient();

        var formats = await FormatsOfTheSeedTenantAsync(factory);

        var order = Assert.Single(formats);
        Assert.Equal("order", order.DocumentType);
        Assert.Equal("PW", order.Prefix);
        Assert.False(order.IncludeYear);
        Assert.Equal(string.Empty, order.YearSeparator);
        Assert.Equal(1, order.MinDigits);
    }

    // La app reinicia sola en k8s, asi que la semilla corre muchas veces sobre la misma base. Se
    // llama al orquestador de nuevo en vez de levantar otra factory: es lo que hace un reinicio de pod.
    [Fact]
    public async Task SeedingTwiceKeepsASingleOrderFormat()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using var factory = WithSeed(baseFactory);
        using var client = factory.CreateClient();

        await factory.Services.RunQepSeedAsync(TestContext.Current.CancellationToken);

        var order = Assert.Single(await FormatsOfTheSeedTenantAsync(factory));
        Assert.Equal("PW", order.Prefix);
    }

    // La fila se puede haber cambiado a mano con el runbook del README. El seeder solo crea: si ya hay
    // formato de pedidos para el tenant, lo deja como esta, aunque no sea el de la semilla.
    [Fact]
    public async Task SeedDoesNotOverwriteAnOrderFormatSetByHand()
    {
        await using var database = await StartDatabaseAsync();
        using var baseFactory = new QepApiFactory(database.GetConnectionString());
        using (baseFactory.CreateClient())
        {
            await DocumentNumberingFormatLookupTests.SetDocumentNumberFormatAsync(
                baseFactory, TenancySeeder.SeedTenantId, "order", "OB-", includeYear: true, "/", 6);
        }

        using var factory = WithSeed(baseFactory);
        using var client = factory.CreateClient();

        var order = Assert.Single(await FormatsOfTheSeedTenantAsync(factory));
        Assert.Equal("OB-", order.Prefix);
        Assert.True(order.IncludeYear);
        Assert.Equal("/", order.YearSeparator);
        Assert.Equal(6, order.MinDigits);
    }

    private static WebApplicationFactory<Program> WithSeed(QepApiFactory factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Seed:Enabled", "true");
            builder.UseSetting("Seed:OwnerEmail", OwnerEmail);
        });

    private static async Task<List<DocumentNumberingFormat>> FormatsOfTheSeedTenantAsync(
        WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        return await dbContext.DocumentNumberingFormats
            .AsNoTracking()
            .Where(format => format.TenantId == TenancySeeder.SeedTenantId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
