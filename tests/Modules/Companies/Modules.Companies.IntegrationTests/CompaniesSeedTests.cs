using Bootstrapper.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Companies.Infrastructure.Persistence;
using Modules.Companies.Infrastructure.Seed;
using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Tenancy.Infrastructure.Seed;
using Testcontainers.PostgreSql;

namespace Modules.Companies.IntegrationTests;

// Lo que la unitaria de los datos no puede cubrir: que las cinco empresas **se persistan**. Son
// las dos mitades que viven fuera del agregado — la FK de city_id contra geography.cities, que
// exige que el DIVIPOLA ya se haya importado, y la coleccion owned de cuentas, que va a su propia
// tabla.
public sealed class CompaniesSeedTests
{
    // En el orden en que vienen en la tabla del owner: primero la de Panamá, despues la de
    // ahorros. El orden de la coleccion owned es el de carga, y el detalle de la cotizacion lo
    // respeta al ofrecer a que cuenta se paga.
    private static readonly string[] ArmoniaCurrencies = ["USD", "COP"];

    [Fact]
    public async Task SeedCreatesTheFiveCompaniesInRionegro()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CompaniesDbContext>();

        var companies = await dbContext.Companies
            .OrderBy(company => company.CreatedAt)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, companies.Count);
        Assert.All(
            companies,
            company => Assert.Equal(TenancySeeder.SeedTenantId, company.TenantId));
        Assert.All(companies, company => Assert.True(company.IsActive));

        // Las cinco viven en la misma sede, asi que comparten ciudad. Que sea **esa** ciudad se
        // afirma contra el DIVIPOLA: 05615 es Rionegro, Antioquia.
        var cityId = Assert.Single(companies.Select(company => company.CityId).Distinct());
        var city = await scope.ServiceProvider.GetRequiredService<ICityRepository>()
            .FindAsync(new CityId(cityId), TestContext.Current.CancellationToken);
        Assert.NotNull(city);
        Assert.Equal("05615", city.DivipolaCode);

        // Seis cuentas en cinco empresas: Armonia Cosmetica tiene dos. La coleccion es owned, asi
        // que EF la trae con el agregado sin Include.
        Assert.Equal(6, companies.Sum(company => company.BankAccounts.Count));

        var armonia = companies.Single(company => company.TaxId == "901.851.609-4");
        Assert.Equal("Armonía Cosmética", armonia.Name);
        Assert.Equal(
            ArmoniaCurrencies,
            armonia.BankAccounts.Select(account => account.Currency).ToArray());
        Assert.Equal(
            "80100033226 SWIFT (COLOPAPAXXX)",
            armonia.BankAccounts[0].AccountNumber);

        // Ocho de los nombres y direcciones llevan tilde; si el ida y vuelta a PostgreSQL se hace
        // con la codificacion equivocada, esta es la asercion que lo detecta.
        var raices = companies.Single(company => company.TaxId == "901.846.471-5");
        Assert.Equal("Raíces Orgánicas", raices.Name);
        Assert.Equal(
            "Zona E, centro logístico, bodega 16 del cruce del tablazo 900 mtrs vía zona franca. "
            + "Rionegro, Antioquia",
            raices.Address);
        Assert.Equal("604 296 6310", raices.Phone);
        Assert.Null(raices.Email);
    }

    // La app reinicia sola en k8s, asi que la semilla corre muchas veces sobre la misma base. Se
    // llama al orquestador de nuevo sobre la misma base en vez de levantar una segunda factory:
    // eso es exactamente lo que hace un reinicio de pod.
    [Fact]
    public async Task SeedingTwiceLeavesTheSameCompanies()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await factory.Services.RunQepSeedAsync(TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CompaniesDbContext>();

        var companies = await dbContext.Companies.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, companies.Count);
        // El seeder es create-only por NIT: la segunda corrida no vuelve a tocar una empresa que
        // ya existe, asi que tampoco le duplica las cuentas.
        Assert.Equal(6, companies.Sum(company => company.BankAccounts.Count));
        Assert.All(companies, company => Assert.Equal(1, company.Version));
    }

    // La ciudad se resuelve perezosa: es un prerrequisito de **construir** las empresas que
    // faltan, no del arranque. Con las cinco ya sembradas no hay nada que construir, asi que
    // tampoco hay por que consultar a Geography — y mucho menos tumbar el arranque si esa
    // consulta fallara. Un resolvedor que revienta es la unica forma de afirmar que no se llamo:
    // si se llamara, la prueba se cae con esa misma excepcion.
    [Fact]
    public async Task SeedDoesNotResolveTheCityWhenEveryCompanyIsAlreadyThere()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: true);
        using var client = factory.CreateClient();

        await factory.Services.SeedCompaniesAsync(
            TenancySeeder.SeedTenantId,
            _ => throw new InvalidOperationException(
                "The city must not be resolved when there is nothing to seed."),
            TestContext.Current.CancellationToken);

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CompaniesDbContext>();
        Assert.Equal(5, await dbContext.Companies.CountAsync(TestContext.Current.CancellationToken));
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
            // Notifications falla al arrancar y todas las pruebas del archivo mueren antes de su
            // aserción.
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
