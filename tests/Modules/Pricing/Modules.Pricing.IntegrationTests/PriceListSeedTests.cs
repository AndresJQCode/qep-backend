using System.Net.Http.Json;
using Modules.Pricing.Infrastructure;
using Modules.Tenancy.Infrastructure;
using static Modules.Pricing.IntegrationTests.PricingApiHarness;

namespace Modules.Pricing.IntegrationTests;

/// <summary>
/// El seed de las cinco listas de precio por defecto (MIN/MAY/DIS/INS/VIP), una por tenant.
/// Corre en cada arranque de la app (<c>InitializePricingDatabaseAsync</c>), así que tiene que
/// ser idempotente: sembrar dos veces sobre el mismo tenant no puede duplicar filas ni violar el
/// índice único de <c>(tenant_id, prefix)</c>. Mismo criterio que
/// <c>GeographySeedIntegrityTests</c>.
/// </summary>
public sealed class PriceListSeedTests
{
    [Fact]
    public async Task SeedingCreatesTheFiveDefaultListsForEachExistingTenant()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Fuerza a que el host arranque (y con él, la migración) antes de insertar el tenant.
        using var warmUpClient = factory.CreateClient();
        await CreateTenantAsync(factory, TenantId);

        await factory.Services.InitializePricingDatabaseAsync(
            TestContext.Current.CancellationToken);

        using var client = CreateManager(factory);
        var body = await ListAsync(client);

        Assert.Equal(5, body.Items.Count);
        Assert.Contains(body.Items, item => item.Prefix == "MIN" && item.Name == "Minorista");
        Assert.Contains(body.Items, item => item.Prefix == "MAY" && item.Name == "Mayorista");
        Assert.Contains(body.Items, item => item.Prefix == "DIS" && item.Name == "Distribuidor");
        Assert.Contains(body.Items, item => item.Prefix == "INS" && item.Name == "Institucional");
        Assert.Contains(body.Items, item => item.Prefix == "VIP" && item.Name == "VIP");
        Assert.All(body.Items, item => Assert.True(item.IsActive));
    }

    [Fact]
    public async Task ReseedingDoesNotDuplicateOrTouchAnAlreadyEditedList()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await CreateTenantAsync(factory, TenantId);
        await factory.Services.InitializePricingDatabaseAsync(TestContext.Current.CancellationToken);

        using var client = CreateManager(factory);
        var seeded = await ListAsync(client);
        var minorista = Assert.Single(seeded.Items, item => item.Prefix == "MIN");
        // El tenant edita el nombre de una lista sembrada; el reseed no debe pisarlo.
        await client.PatchAsJsonAsync(
            $"{PriceListsUrl()}/{minorista.Id}",
            new { name = "Minorista Editado", prefix = "MIN" },
            TestContext.Current.CancellationToken);

        await factory.Services.InitializePricingDatabaseAsync(TestContext.Current.CancellationToken);

        var afterReseed = await ListAsync(client);
        Assert.Equal(5, afterReseed.Items.Count);
        var editedStillThere = Assert.Single(afterReseed.Items, item => item.Prefix == "MIN");
        Assert.Equal("Minorista Editado", editedStillThere.Name);
    }

    // TenancyDatabaseInitializer auto-provisiona un tenant de conveniencia
    // (DevelopmentTenantId) apenas la app arranca en Development, si tenancy.tenants está
    // vacía — no es parte de este slice, es una comodidad de desarrollo ya existente. Esta
    // prueba ancla que DefaultPriceListsSeeder lo alcanza igual que a cualquier otro tenant: es
    // justo el descubrimiento que costó depurar el falso 422 de "name_taken" en
    // PriceListApiTests antes de que TenantId de este harness dejara de coincidir con él.
    [Fact]
    public async Task TheAutoProvisionedDevelopmentTenantAlsoGetsTheFiveDefaultLists()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // El warm-up ya alcanza: InitializeTenancyDatabaseAsync crea el tenant de desarrollo e
        // InitializePricingDatabaseAsync lo siembra, los dos como parte del arranque normal de
        // Program.cs — sin ningún CreateTenantAsync ni segunda llamada al inicializador.
        using var warmUpClient = factory.CreateClient();

        using var devTenantClient = CreateClient(
            factory,
            SubjectId,
            TenancyDatabaseInitializer.DevelopmentTenantId.ToString(),
            "pricing.price_list.read");
        var body = await ListAsync(devTenantClient, TenancyDatabaseInitializer.DevelopmentTenantId.ToString());

        Assert.Equal(5, body.Items.Count);
    }

    // TenantId (el de este harness, ...0101) nunca se crea en esta prueba. Distinto de
    // TenancyDatabaseInitializer.DevelopmentTenantId (...0001, ver el comentario en
    // PricingApiHarness): ese sí se auto-provisiona en Development apenas la app arranca —
    // por eso el warm-up de abajo deja sembradas sus cinco listas igual, pero nunca las de un
    // tenant que no tiene fila, que es justo lo que esta prueba comprueba.
    [Fact]
    public async Task TenantsWithoutAnyRowGetNoDefaultListsSeeded()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var warmUpClient = factory.CreateClient();
        await factory.Services.InitializePricingDatabaseAsync(TestContext.Current.CancellationToken);

        using var client = CreateManager(factory);
        var body = await ListAsync(client);

        Assert.Empty(body.Items);
    }
}
