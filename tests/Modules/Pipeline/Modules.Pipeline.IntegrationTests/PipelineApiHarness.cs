using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Customers.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Modules.Pipeline.Application;
using Modules.Pipeline.Infrastructure.Messaging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Pipeline.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de Pipeline (spec 2026-10-13), copia de <c>MessagingApiHarness</c>: contenedor compartido,
/// plantilla migrada, una base por prueba y un host que fija sus claves con <c>UseSetting</c>; nunca las hereda de los
/// user-secrets de quien corre las pruebas (memoria «user-secrets rompen las pruebas de integración»). Clientes y
/// conversaciones se siembran por los servicios del host (Customers por <c>ICustomerWhatsAppDirectory</c>, Messaging por su
/// <c>DbContext</c>): Pipeline los lee por adaptadores, nunca por su API.
/// </summary>
internal static class PipelineApiHarness
{
    public static readonly string[] ReadPermissions = [PipelinePermissions.DealRead];

    public static readonly string[] ManagePermissions = [PipelinePermissions.DealRead, PipelinePermissions.DealManage];

    public static readonly string[] AllPermissions =
        [PipelinePermissions.DealRead, PipelinePermissions.DealManage, PipelinePermissions.SettingsManage];

    private const string TemplateDatabase = "qep_template_pipeline";

    // CA1869: las opciones se reutilizan. camelCase, como el payload que escribe Quotations.
    private static readonly JsonSerializerOptions OutboxJson = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    // Postgres rechaza dos CREATE DATABASE simultáneos desde la misma plantilla: los clones van de a uno.
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

    public static string PipelineUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/pipeline";

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
            // Fijados, nunca heredados (mismo criterio que MessagingApiHarness y QuotationsApiHarness).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
            builder.UseSetting("Quotations:WhatsApp:ApiToken", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:FromNumber", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:TemplateId", string.Empty);
            builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", "true");
            // Los workers de Messaging no corren solos durante una prueba de Pipeline.
            builder.UseSetting("Messaging:Workers:DeliveryPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:MediaPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:CampaignPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:PurgeIntervalHours", "24");
            builder.ConfigureServices(services =>
            {
                // El worker toma mensajes cada 3 s por su cuenta: en una prueba competiría con DrainQuotationEventsAsync y la
                // volvería no determinista. Se saca el hosted service; el procesador (singleton) sigue registrado.
                var workers = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType == typeof(QuotationEventsWorker))
                    .ToList();
                foreach (var descriptor in workers)
                {
                    services.Remove(descriptor);
                }
            });
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
                displayName = "Pipeline Test Org",
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

    /// <summary>Una membresía más del tenant, escrita directo (sin invitación), como <c>MessagingApiHarness.SeedMemberAsync</c>.</summary>
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

    /// <summary>D3: <c>pipeline</c> no viene con el signup; se prende con su fila, como <c>EnableMessagingAsync</c>.</summary>
    public static Task<int> EnablePipelineAsync(string connectionString, Guid tenantId) => EnableModuleAsync(connectionString, tenantId, "pipeline");

    public static Task<int> EnableMessagingAsync(string connectionString, Guid tenantId) => EnableModuleAsync(connectionString, tenantId, "messaging");

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

    /// <summary>Un cliente <c>Incomplete</c> como lo crea la ingesta de WhatsApp (H18), con el nombre que Customers le dio.</summary>
    public static async Task<(Guid Id, string Name)> SeedCustomerAsync(WebApplicationFactory<Program> factory, Guid tenantId, string profileName)
    {
        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<ICustomerWhatsAppDirectory>();
        var ensured = await directory.EnsureAsync(
            tenantId, new WhatsAppContact($"CO.{Guid.NewGuid():N}", null, profileName, null), TestContext.Current.CancellationToken);
        var refs = await directory.FindRefsAsync(tenantId, [ensured.CustomerId], TestContext.Current.CancellationToken);
        return (ensured.CustomerId, refs[ensured.CustomerId].Name);
    }

    /// <summary>Una conversación con BSUID, atada al cliente si se pasa, con sus marcas de tiempo como las dejaría la ingesta.
    /// La conexión es un id suelto: Messaging no tiene FK a Integrations.</summary>
    public static async Task<Guid> SeedConversationAsync(
        WebApplicationFactory<Program> factory, string connectionString, Guid tenantId, Guid? customerId,
        DateTimeOffset? lastInboundAt = null, DateTimeOffset? lastMessageAt = null, bool resolved = false)
    {
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var conversation = Conversation.StartWithUserId(
                Guid.CreateVersion7(), tenantId, Guid.CreateVersion7(), $"CO.{Guid.NewGuid():N}", null, "Laura", DateTimeOffset.UtcNow);
            dbContext.Conversations.Add(conversation);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = conversation.Id;
        }

        await ExecuteAsync(connectionString,
            """
            UPDATE messaging.conversations
            SET customer_id = @customerId, last_inbound_at = @inbound, last_message_at = @message, status = @status
            WHERE id = @id
            """,
            ("customerId", (object?)customerId ?? DBNull.Value),
            ("inbound", (object?)lastInboundAt?.ToUniversalTime() ?? DBNull.Value),
            ("message", (object?)lastMessageAt?.ToUniversalTime() ?? DBNull.Value),
            ("status", resolved ? "Resolved" : "Open"),
            ("id", id));
        return id;
    }

    /// <summary>Un evento de Quotations en el outbox de plataforma, como lo escribe <c>QuotationLifecycleEventPublisher</c>
    /// (payload camelCase, Global Constraints del plan).</summary>
    public static async Task<Guid> InsertOutboxAsync(string connectionString, string eventName, object payload)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts, last_error)
            VALUES (@id, @name, CAST(@payload AS jsonb), @correlation, now(), NULL, 0, NULL)
            """,
            ("id", id),
            ("name", eventName),
            ("payload", JsonSerializer.Serialize(payload, OutboxJson)),
            ("correlation", id.ToString()));
        return id;
    }

    /// <summary>Una pasada del worker de eventos de Quotations: reclama y procesa lo pendiente.</summary>
    public static Task DrainQuotationEventsAsync(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<QuotationEventsProcessor>().ProcessBatchAsync(TestContext.Current.CancellationToken);

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
