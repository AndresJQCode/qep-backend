using System.Net;
using System.Net.Http.Json;
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

    /// <summary>
    /// Un cliente del stub con permisos explícitos por <c>X-Permissions</c>, como <c>OperatorConsoleApiTests</c>: el stub
    /// sólo concede los de tenancy por defecto, y el filtro de operador deja vivos los <c>operator.*</c> sólo en el tenant
    /// configurado como operador.
    /// </summary>
    public static HttpClient StubClient(QepApiFactory factory, Guid subjectId, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(TestContext.Current.CancellationToken);
        Assert.Equal(code, problem?.Code);
    }

    public sealed record ProblemPayload(string? Code);

    public sealed class QepApiFactory(string connectionString, Guid? operatorTenantId = null)
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
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Siempre fijado, aunque la prueba no pida operador: un Platform:OperatorTenantId en los user-secrets de quien corre
            // las pruebas convertiría en operador a un tenant ajeno. Vacío vale lo mismo que ausente.
            builder.UseSetting("Platform:OperatorTenantId", operatorTenantId?.ToString() ?? string.Empty);
        }
    }
}
