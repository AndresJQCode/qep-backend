using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Modules.Geography.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo, calcado de
/// <c>CompaniesApiHarness</c>. Geography no tiene conceptos de tenant ni permiso propios —los
/// datos son globales— pero el stub de desarrollo (<c>DevelopmentAuthenticationHandler</c>) exige
/// igual los headers <c>X-Subject-Id</c> **y** <c>X-Tenant-Id</c> para autenticar cualquier
/// request, aunque el tenant nunca se use acá: sin los dos, ni siquiera se llega a
/// <c>RequireAuthorization()</c>.
/// </summary>
internal static class GeographyApiHarness
{
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string TenantId = "01900000-0000-7000-8000-000000000001";

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    public static HttpClient CreateAuthenticatedClient(QepApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        return client;
    }

    public sealed class QepApiFactory(string connectionString)
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves de Infobip
            // ausentes, NotificationsOptionsValidator falla al arrancar y todas las pruebas de
            // este proyecto mueren antes de llegar a su aserción.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
