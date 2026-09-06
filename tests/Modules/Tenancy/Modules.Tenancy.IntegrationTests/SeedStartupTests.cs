using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class SeedStartupTests
{
    // La semilla crea un tenant y otorga admin. Que esté apagada por defecto es la única
    // defensa que tiene el ambiente desplegado, así que se prueba explícitamente.
    [Fact]
    public async Task SeedDisabledCreatesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), seedEnabled: false);
        using var client = factory.CreateClient();

        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var seeded = await dbContext.Tenants
            .AnyAsync(tenant => tenant.Slug == "origen-botanico", TestContext.Current.CancellationToken);

        Assert.False(seeded);
    }

    // Prendida sin email no se puede sembrar la membresía, y un tenant al que nadie puede
    // entrar es peor que no sembrar nada. Mismo criterio que la cadena de conexión: fallar
    // con un mensaje que dice qué falta.
    [Fact]
    public async Task SeedEnabledWithoutOwnerEmailFailsStartup()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(), seedEnabled: true, ownerEmail: string.Empty);

        // ThrowsAny y no Throws<OptionsValidationException>: ValidateOnStart lanza durante
        // host.StartAsync(), y WebApplicationFactory puede entregarla envuelta. Lo que se afirma
        // es que el arranque muere y que el mensaje nombra la clave que falta, no el tipo exacto.
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        Assert.Contains(messages, message => message.Contains("Seed:OwnerEmail", StringComparison.Ordinal));
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
