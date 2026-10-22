using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Automations.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de Automations (spec 2026-10-21), copia de <c>PipelineApiHarness</c>: contenedor compartido,
/// plantilla migrada, una base por prueba y un host que fija sus claves con <c>UseSetting</c>; nunca las hereda de los
/// user-secrets de quien corre las pruebas (memoria «user-secrets rompen las pruebas de integración»). Las conexiones se
/// siembran por los servicios de Integrations del host, como en <c>MessagingApiHarness</c>: Automations las lee por
/// adaptadores, nunca por su API.
/// </summary>
internal static class AutomationsApiHarness
{
    /// <summary>Centinelas inventados, los mismos de <c>MessagingApiHarness</c>: una prueba de fugas los persigue por
    /// respuestas y logs.</summary>
    public const string SentinelMetaAccessToken = "meta-access-token-SENTINEL-m3";

    public const string SentinelInstagramAccessToken = "ig-access-token-SENTINEL-m5";

    public static readonly string[] ReadPermissions = [AutomationsPermissions.ChatbotRead];

    public static readonly string[] ManagePermissions = [AutomationsPermissions.ChatbotRead, AutomationsPermissions.ChatbotManage];

    private const string TemplateDatabase = "qep_template_automations";

    /// <summary>La llave con la que el host cifra los tokens de las conexiones. Calculada, no escrita.</summary>
    public static string TestSecretProtectionKey { get; } =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    // Postgres rechaza dos CREATE DATABASE simultáneos desde la misma plantilla: los clones van de a uno.
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

    public static string AutomationsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/automations";

    /// <summary>Una base limpia y migrada para esta prueba. Se borra en <c>DisposeAsync</c>.</summary>
    public static async Task<TestDatabase> StartDatabaseAsync()
    {
        var server = await SharedServer.Value;
        var name = $"t_{Guid.CreateVersion7():N}";
        await CloneGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await ExecuteAdminAsync(server, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");
        }
        finally
        {
            CloneGate.Release();
        }

        return new TestDatabase(server, name);
    }

    private static async Task<PostgreSqlContainer> StartServerAsync()
    {
        var server = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .WithCommand("-c", "max_connections=400")
            .Build();
        await server.StartAsync(CancellationToken.None);
        await ExecuteAdminAsync(server, $"CREATE DATABASE \"{TemplateDatabase}\"");

        // El host migra al arrancar: arrancarlo una vez contra la plantilla la deja con todo.
        using (var factory = new QepApiFactory(ConnectionStringFor(server, TemplateDatabase)))
        using (factory.CreateClient())
        {
        }

        // Una conexión viva a la plantilla haría fallar todos los clones.
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync(server, $"ALTER DATABASE \"{TemplateDatabase}\" WITH ALLOW_CONNECTIONS false");
        return server;
    }

    private static string ConnectionStringFor(PostgreSqlContainer server, string database) =>
        new NpgsqlConnectionStringBuilder(server.GetConnectionString()) { Database = database }.ConnectionString;

    private static async Task ExecuteAdminAsync(PostgreSqlContainer server, string sql)
    {
        await using var connection = new NpgsqlConnection(server.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>La base de una prueba.</summary>
    public sealed class TestDatabase(PostgreSqlContainer server, string name) : IAsyncDisposable
    {
        public string GetConnectionString() => ConnectionStringFor(server, name);

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(GetConnectionString()));
            await ExecuteAdminAsync(server, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    public sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 40 }.ConnectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijados, nunca heredados (mismo criterio que PipelineApiHarness y MessagingApiHarness).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
            builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", "true");
            // Las conexiones sembradas se cifran con esta llave (SeedWhatsAppConnectionAsync, SeedInstagramConnectionAsync).
            builder.UseSetting("Integrations:SecretProtection:ActiveKeyId", "test");
            builder.UseSetting("Integrations:SecretProtection:Keys:test", TestSecretProtectionKey);
            builder.UseSetting("Integrations:SecretProtection:Keys:k1", string.Empty);
            // Los workers de Messaging no corren solos durante una prueba de Automations.
            builder.UseSetting("Messaging:Workers:DeliveryPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:MediaPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:CampaignPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:PurgeIntervalHours", "24");
        }
    }

    internal sealed record RegisteredTenant(Guid TenantId, Guid OwnerUserId);

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory, Guid subjectId, Guid tenantId, params string[] permissions)
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

    /// <summary>Un tenant real, con el dueño y su membresía activa: los handlers resuelven la membresía de quien llama.</summary>
    public static async Task<RegisteredTenant> RegisterTenantAsync(WebApplicationFactory<Program> factory)
    {
        var email = $"owner-{Guid.CreateVersion7():N}@example.com";
        using var bootstrap = CreateClient(factory, Guid.CreateVersion7(), Guid.CreateVersion7());
        bootstrap.DefaultRequestHeaders.Add("X-Email", email);
        bootstrap.DefaultRequestHeaders.Add("X-Email-Verified", "true");
        var response = await bootstrap.PostAsJsonAsync(
            "/api/v1/auth/register-tenant",
            new
            {
                displayName = "Automations Test Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisterTenantResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return new RegisteredTenant(registered.TenantId, registered.OwnerUserId);
    }

    public static Task<Guid> OwnerMembershipIdAsync(string connectionString, RegisteredTenant tenant) =>
        ScalarAsync<Guid>(
            connectionString,
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @tenantId AND user_id = @userId",
            ("tenantId", tenant.TenantId),
            ("userId", tenant.OwnerUserId));

    /// <summary>Una membresía más del tenant, escrita directo (sin invitación), como <c>PipelineApiHarness.SeedMemberAsync</c>.</summary>
    public static async Task<(Guid MembershipId, Guid UserId)> SeedMemberAsync(
        string connectionString, Guid tenantId, string? displayName, string role = "advisor", string state = "Active")
    {
        var membershipId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO tenancy.memberships (id, user_id, tenant_id, state, roles, origin, invited_at, accepted_at, expires_at, version, created_at, updated_at, display_name)
            VALUES (@id, @userId, @tenantId, @state, ARRAY[@role]::text[], 'invitation', now(), now(), now() + interval '3 days', 1, now(), now(), @name)
            """,
            ("id", membershipId), ("userId", userId), ("tenantId", tenantId), ("role", role), ("state", state),
            ("name", displayName ?? (object)DBNull.Value));
        return (membershipId, userId);
    }

    /// <summary>Spec 2026-10-21 §4.1: <c>automations</c> depende de <c>messaging</c> y no viene con el signup; se prenden los dos
    /// con su fila, como <c>PipelineApiHarness.EnablePipelineAsync</c>.</summary>
    public static async Task EnableAutomationsAsync(string connectionString, Guid tenantId)
    {
        await EnableModuleAsync(connectionString, tenantId, "messaging");
        await EnableModuleAsync(connectionString, tenantId, "automations");
    }

    public static Task<int> DisableModuleAsync(string connectionString, Guid tenantId, string moduleKey) =>
        ExecuteAsync(connectionString,
            "UPDATE tenancy.tenant_modules SET status = 'inactive', status_changed_at = now() WHERE tenant_id = @tenantId AND module_key = @key",
            ("tenantId", tenantId), ("key", moduleKey));

    private static Task<int> EnableModuleAsync(string connectionString, Guid tenantId, string moduleKey) =>
        ExecuteAsync(connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, @key, now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", tenantId), ("key", moduleKey));

    /// <summary>Una conexión whatsapp-cloud con su ruta, escrita directo por el repositorio de Integrations (sin Meta), cifrada
    /// con la llave activa del host. Copia de <c>MessagingApiHarness.SeedWhatsAppConnectionAsync</c>.</summary>
    public static async Task<Guid> SeedWhatsAppConnectionAsync(
        WebApplicationFactory<Program> host, Guid tenantId, string name, string phoneNumberId, string wabaId,
        string accessToken = SentinelMetaAccessToken)
    {
        using var scope = host.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var connection = IntegrationConnection.Create(
            IntegrationProviders.WhatsAppCloud,
            tenantId,
            name,
            new Dictionary<string, string>
            {
                [WhatsAppCloudFieldKeys.DisplayPhoneNumber] = "+57 300 123 4567",
                [WhatsAppCloudFieldKeys.VerifiedName] = "Prueba",
                [WhatsAppCloudFieldKeys.PhoneNumberId] = phoneNumberId,
                [WhatsAppCloudFieldKeys.WabaId] = wabaId,
                [WhatsAppCloudFieldKeys.QualityRating] = "GREEN",
            },
            new Dictionary<string, string> { [WhatsAppCloudFieldKeys.AccessToken] = accessToken },
            protector.Protect,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
        scope.ServiceProvider.GetRequiredService<IConnectionRouteRepository>().Add(
            IntegrationConnectionRoute.Create(IntegrationProviders.WhatsAppCloud.Key, phoneNumberId, wabaId, tenantId, connection.Id));
        await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection.Id;
    }

    /// <summary>Spec 2026-10-14: una conexión <c>instagram</c> con su ruta por el id de la cuenta, escrita directo por el
    /// repositorio de Integrations (sin Meta). Copia de <c>MessagingApiHarness.SeedInstagramConnectionAsync</c>.</summary>
    public static async Task<Guid> SeedInstagramConnectionAsync(
        WebApplicationFactory<Program> host, Guid tenantId, string name, string igUserId, string username = "origenbotanico",
        string accessToken = SentinelInstagramAccessToken)
    {
        using var scope = host.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var connection = IntegrationConnection.Create(
            IntegrationProviders.Instagram,
            tenantId,
            name,
            new Dictionary<string, string>
            {
                [InstagramFieldKeys.Username] = username,
                [InstagramFieldKeys.AccountName] = "Origen Botánico",
                [InstagramFieldKeys.IgUserId] = igUserId,
                [InstagramFieldKeys.TokenExpiresAt] = DateTimeOffset.UtcNow.AddDays(60).ToString("O"),
            },
            new Dictionary<string, string> { [InstagramFieldKeys.AccessToken] = accessToken },
            protector.Protect,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
        scope.ServiceProvider.GetRequiredService<IConnectionRouteRepository>().Add(
            IntegrationConnectionRoute.Create(IntegrationProviders.Instagram.Key, igUserId, null, tenantId, connection.Id));
        await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection.Id;
    }

    /// <summary>El <c>code</c> y las claves de <c>errors</c> de un ProblemDetails.</summary>
    public static async Task<(string? Code, string[] ErrorKeys)> ProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
        var keys = root.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        return (code, keys);
    }

    public static Task<long> CountAsync(string connectionString, string sql, params (string Name, object Value)[] parameters) =>
        ScalarAsync<long>(connectionString, sql, parameters);

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
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

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);
}
