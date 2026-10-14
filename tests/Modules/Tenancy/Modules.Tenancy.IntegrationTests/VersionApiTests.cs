using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// <c>GET /api/v1/version</c> (spec 2026-10-14): qué build corre en el pod, para verificar un
/// despliegue desde <c>curl.exe</c> o desde la SPA sin abrir Azure DevOps ni <c>kubectl</c>.
/// Anónimo porque el caso de uso principal es mirar qué hay desplegado cuando el login puede
/// estar roto; limitado por IP como el documento OpenAPI, que es la otra superficie pública.
/// </summary>
/// <remarks>
/// Vive acá, junto a <c>ReadinessApiTests</c> y <c>CorsApiTests</c>, porque es transversal a la
/// API y no de un módulo. Mismo arranque que ellas: la app corre las migraciones antes de
/// mapear endpoints, así que hace falta la base aunque el endpoint no la toque.
/// </remarks>
public sealed class VersionApiTests
{
    // Mismo valor que la ventana fija de Program.cs: se agota con exactamente estas respuestas 200.
    private const int PublicPermitLimit = 120;

    private const string Commit = "e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3";

    [Fact]
    public async Task AnswersAnonymouslyWithTheConfiguredBuild()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new VersionApiFactory(database.GetConnectionString(), Commit);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/v1/version", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<VersionBody>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("4812", body.BuildId);
        Assert.Equal("20261014.3", body.BuildNumber);
        Assert.Equal(Commit, body.Commit);
        Assert.Equal("Development", body.Environment);
        Assert.InRange(body.StartedAt, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow);
    }

    // Un token que la plantilla del pipeline no reemplazó llega literal al pod. El endpoint lo
    // trata como ausente: publicar "#{Build.SourceVersion}#" como si fuera un commit sería peor
    // que no saberlo.
    [Fact]
    public async Task AnUnresolvedPipelineTokenIsReportedAsUnknown()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new VersionApiFactory(database.GetConnectionString(), "#{Build.SourceVersion}#");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/api/v1/version", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<VersionBody>(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("4812", body.BuildId);
        Assert.Null(body.Commit);
    }

    // Prueba que la política `public` está puesta, no sólo el AllowAnonymous: es lo que acota el
    // scraping de una superficie que no pide sesión.
    [Fact]
    public async Task IsRateLimitedLikeTheOtherPublicSurfaces()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new VersionApiFactory(database.GetConnectionString(), Commit);

        for (var i = 0; i < PublicPermitLimit; i++)
        {
            Assert.Equal(StatusCodes.Status200OK, await GetVersionAsync(factory));
        }

        Assert.Equal(StatusCodes.Status429TooManyRequests, await GetVersionAsync(factory));
    }

    private static async Task<int> GetVersionAsync(VersionApiFactory factory)
    {
        var context = await factory.Server.SendAsync(
            http =>
            {
                http.Request.Method = HttpMethods.Get;
                http.Request.Path = "/api/v1/version";
                http.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
            },
            TestContext.Current.CancellationToken);
        return context.Response.StatusCode;
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

    private sealed record VersionBody(
        string? BuildId,
        string? BuildNumber,
        string? Commit,
        string Environment,
        DateTimeOffset StartedAt);

    private sealed class VersionApiFactory(string connectionString, string commit)
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
            builder.UseSetting("Deployment:BuildId", "4812");
            builder.UseSetting("Deployment:BuildNumber", "20261014.3");
            builder.UseSetting("Deployment:Commit", commit);
        }
    }
}
