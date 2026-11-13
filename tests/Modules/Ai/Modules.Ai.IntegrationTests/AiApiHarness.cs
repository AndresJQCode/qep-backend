using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Ai.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo, copia de <c>SuppliersApiHarness</c>: un tenant con fila (sin ella no
/// hay máscara de módulos) y <c>configureServices</c> en la fábrica, para los dobles de los proveedores que llegan con las tareas
/// siguientes.
/// </summary>
internal static class AiApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";

    /// <summary>La llave con la que el host cifra. Calculada, no escrita: un literal con forma de llave en el repo termina copiado a un
    /// ambiente. Bytes 0..31, la misma que IntegrationsApiHarness.</summary>
    public static string TestSecretProtectionKey { get; } =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    /// <summary>El nombre del cliente del probador de la clave de Anthropic (<c>AnthropicConnectionTester.HttpClientName</c>, interno de
    /// Integrations): la prueba le cambia el handler primario por uno de mentira, nunca sale a api.anthropic.com.</summary>
    public const string AnthropicTesterClient = "integrations.anthropic";

    public static string AiUrl(string tenantId = TenantId) => $"/api/v1/tenants/{tenantId}/ai";

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

    /// <summary>Siembra la fila del tenant y sus módulos; con fila, el stub de desarrollo enmascara por módulo como el real.</summary>
    public static async Task SeedTenantAsync(QepApiFactory factory, IEnumerable<TenantModuleKey>? modules = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(TenantId));
        var now = DateTimeOffset.UtcNow;
        if (await tenancy.Tenants.FindAsync([id], TestContext.Current.CancellationToken) is null)
        {
            tenancy.Tenants.Add(Tenant.Create(
                id, "ai-api-tests", "Ai API Tests", "es-CO", "America/Bogota", "yyyy-MM-dd", MembershipId.New(), now));
            await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // La membresía activa de quien llama: Integrations atribuye cada conexión a una (sin ella, 403). Escrita directo, como
        // AutomationsApiHarness.SeedMemberAsync, e idempotente para poder sembrar dos veces.
        await tenancy.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO tenancy.memberships (id, user_id, tenant_id, state, roles, origin, invited_at, accepted_at, expires_at, version, created_at, updated_at, display_name)
            SELECT {Guid.CreateVersion7()}, {Guid.Parse(SubjectId)}, {id.Value}, 'Active', ARRAY['admin']::text[], 'invitation', now(), now(), now() + interval '3 days', 1, now(), now(), 'Admin de pruebas'
            WHERE NOT EXISTS (SELECT 1 FROM tenancy.memberships WHERE tenant_id = {id.Value} AND user_id = {Guid.Parse(SubjectId)})
            """,
            TestContext.Current.CancellationToken);

        foreach (var key in modules ?? TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({id.Value}, {key.Value}, {now}, {TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                TestContext.Current.CancellationToken);
        }
    }

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está: el permiso de ai se pide explícito, así cada 403 queda
    // atribuible al permiso, al tenant o al módulo.
    public static HttpClient CreateClient(QepApiFactory factory, string tenantId = TenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", SubjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    public static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAsync(IEnumerable<TenantModuleKey>? modules = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory, modules);
        return (database, factory);
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

    /// <summary>Una escritura con <c>If-Match</c> opcional. El cuerpo va como JSON; <c>null</c> manda la petición sin cuerpo.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body = null, long? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (ifMatch is { } version)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Una conexión whatsapp-cloud escrita directo por el repositorio de Integrations (sin Meta), cifrada con la llave del host.
    /// Copia de <c>AutomationsApiHarness.SeedWhatsAppConnectionAsync</c>.</summary>
    public static Task<Guid> SeedWhatsAppConnectionAsync(QepApiFactory factory, string name, string phoneNumberId, string tenantId = TenantId) =>
        SeedConnectionAsync(
            factory,
            IntegrationProviders.WhatsAppCloud,
            tenantId,
            name,
            new Dictionary<string, string>
            {
                [WhatsAppCloudFieldKeys.DisplayPhoneNumber] = "+57 300 123 4567",
                [WhatsAppCloudFieldKeys.VerifiedName] = "Prueba",
                [WhatsAppCloudFieldKeys.PhoneNumberId] = phoneNumberId,
                [WhatsAppCloudFieldKeys.WabaId] = $"9{phoneNumberId}",
                [WhatsAppCloudFieldKeys.QualityRating] = "GREEN",
            },
            new Dictionary<string, string> { [WhatsAppCloudFieldKeys.AccessToken] = "meta-access-token-SENTINEL-a1" },
            route: (phoneNumberId, $"9{phoneNumberId}"));

    public static Task<Guid> SeedInstagramConnectionAsync(QepApiFactory factory, string name, string igUserId) =>
        SeedConnectionAsync(
            factory,
            IntegrationProviders.Instagram,
            TenantId,
            name,
            new Dictionary<string, string>
            {
                [InstagramFieldKeys.Username] = "origenbotanico",
                [InstagramFieldKeys.AccountName] = "Origen Botánico",
                [InstagramFieldKeys.IgUserId] = igUserId,
                [InstagramFieldKeys.TokenExpiresAt] = DateTimeOffset.UtcNow.AddDays(60).ToString("O"),
            },
            new Dictionary<string, string> { [InstagramFieldKeys.AccessToken] = "ig-access-token-SENTINEL-a2" },
            route: (igUserId, null));

    /// <summary>La conexión de Anthropic del tenant, sin pasar por el probador (que se ejerce en <c>AiSettingsApiTests</c>).</summary>
    public static Task<Guid> SeedAnthropicConnectionAsync(QepApiFactory factory) =>
        SeedConnectionAsync(
            factory,
            IntegrationProviders.Anthropic,
            TenantId,
            "Cuenta de Anthropic",
            new Dictionary<string, string>(),
            new Dictionary<string, string> { [AnthropicFieldKeys.ApiKey] = "sk-ant-api03-SENTINEL-a3" },
            route: null);

    private static async Task<Guid> SeedConnectionAsync(
        QepApiFactory factory, IntegrationProvider provider, string tenantId, string name, IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets, (string ExternalId, string? WabaId)? route)
    {
        using var scope = factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var tenant = Guid.Parse(tenantId);
        var connection = IntegrationConnection.Create(
            provider, tenant, name, fields, secrets, protector.Protect, Guid.CreateVersion7(), DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
        if (route is { } value)
        {
            scope.ServiceProvider.GetRequiredService<IConnectionRouteRepository>().Add(
                IntegrationConnectionRoute.Create(provider.Key, value.ExternalId, value.WabaId, tenant, connection.Id));
        }

        await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection.Id;
    }

    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is null or DBNull ? default : (T)result;
    }

    public static async Task<int> ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <param name="configureServices">Sustituciones de servicios para la prueba; corre después del registro de la aplicación.</param>
    /// <param name="settings">Claves de configuración que la prueba fija además de las de siempre (por ejemplo, un modelo disponible).</param>
    public sealed class QepApiFactory(
        string connectionString, Action<IServiceCollection>? configureServices = null, IReadOnlyDictionary<string, string>? settings = null)
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
            // Fijado, nunca heredado de appsettings.json: deja explícito qué canal de correo ejerce la prueba.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true.
            builder.UseSetting("Seed:Enabled", "false");
            // Como IntegrationsApiHarness: "test" es la activa y "k1" —el id que el README sugiere para local— se vacía, para que una
            // llave mal pegada en la máquina de quien corre no tumbe las pruebas que guardan una clave de proveedor.
            builder.UseSetting("Integrations:SecretProtection:ActiveKeyId", "test");
            builder.UseSetting("Integrations:SecretProtection:Keys:test", TestSecretProtectionKey);
            builder.UseSetting("Integrations:SecretProtection:Keys:k1", string.Empty);
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}
