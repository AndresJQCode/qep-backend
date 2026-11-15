using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// La readiness del pod mira la base; la liveness no (plan 2026-09-24, tarea 1). Con dos réplicas,
/// un pod que perdió la conexión tiene que salir del Service sin que Kubernetes lo reinicie.
/// </summary>
/// <remarks>
/// El caso 503 apaga la base <b>después</b> de arrancar la app: sin base, el arranque ni siquiera
/// llega a mapear endpoints, porque corre las migraciones antes de <c>RunAsync</c>.
/// </remarks>
public sealed class ReadinessApiTests
{
    [Fact]
    public async Task ReadyRespondsOkWhileTheDatabaseIsUp()
    {
        await using var database = await TenancyTestDatabase.StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadyRespondsServiceUnavailableWhenTheDatabaseGoesDownButLiveStaysOk()
    {
        // Contenedor propio, no el servidor compartido: la prueba lo apaga.
        await using var database = await StartOwnContainerAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        await database.StopAsync(TestContext.Current.CancellationToken);

        using var ready = await client.GetAsync(
            new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        using var live = await client.GetAsync(
            new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        // Si la liveness dependiera de la base, esta caída reiniciaría todos los pods en cadena.
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    // Valkey no entra a /health/ready (spec 2026-11-13, D-1): con las áreas prendidas y nadie escuchando en el puerto,
    // el host arranca y las dos sondas responden 200. Cada área vuelve a su respaldo local.
    [Fact]
    public async Task TheHostStartsWithoutValkey()
    {
        await using var database = await TenancyTestDatabase.StartDatabaseAsync();
        using var factory = new QepApiFactory(
            database.GetConnectionString(),
            new Dictionary<string, string>
            {
                ["Valkey:Enabled"] = "true",
                ["Valkey:Areas:Presence"] = "true",
                ["Valkey:Areas:RateLimiting"] = "true",
                ["ConnectionStrings:Valkey"] = "127.0.0.1:1,connectTimeout=500",
            });
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync(
            new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        using var live = await client.GetAsync(
            new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    private static async Task<PostgreSqlContainer> StartOwnContainerAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private sealed class QepApiFactory(string connectionString, IReadOnlyDictionary<string, string>? settings = null)
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
            // Fijado, nunca heredado: mismo criterio que TenantSettingsApiTests (SDD-CT-17).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }
        }
    }
}
