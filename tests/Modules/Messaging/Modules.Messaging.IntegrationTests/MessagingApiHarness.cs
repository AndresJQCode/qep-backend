using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;
using Modules.Messaging.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Messaging.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de Messaging (spec 2026-10-09), copia de <c>IntegrationsApiHarness</c>:
/// contenedor compartido, plantilla migrada, una base por prueba y un host que fija todas sus claves
/// —incluidos los centinelas de Meta— con <c>UseSetting</c>; nunca las hereda de los user-secrets de
/// quien corre las pruebas (memoria «user-secrets rompen las pruebas de integración»).
/// </summary>
internal static class MessagingApiHarness
{
    /// <summary>Centinelas inventados: una prueba de fugas los persigue por respuestas y logs.</summary>
    public const string TestAppSecret = "meta-app-secret-SENTINEL-m1";

    public const string TestVerifyToken = "meta-verify-token-SENTINEL-m2-0123456789abcdef";

    public const string SentinelMetaAccessToken = "meta-access-token-SENTINEL-m3";

    public const string MetaAppId = "100200300";

    public const string MetaConfigId = "400500600";

    /// <summary>El nombre del cliente de Graph de Integrations (<c>MetaGraphClient.HttpClientName</c>, que
    /// es internal de su módulo).</summary>
    public const string IntegrationsGraphClientName = "integrations.meta-graph";

    public const string MessagingGraphClientName = "messaging.meta-graph";

    public const string WebhookUrl = "/api/webhooks/whatsapp";

    /// <summary>La llave con la que el host cifra los tokens de las conexiones. Calculada, no escrita.</summary>
    public static string TestSecretProtectionKey { get; } =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    public static readonly string[] ReadPermissions = [MessagingPermissions.ConversationRead];

    public static readonly string[] ManagePermissions =
        [MessagingPermissions.ConversationRead, MessagingPermissions.ConversationManage];

    public static string ConversationsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/messaging/conversations";

    public static string ConversationUrl(Guid tenantId, Guid conversationId) => $"{ConversationsUrl(tenantId)}/{conversationId}";

    public static string MessagesUrl(Guid tenantId, Guid conversationId) => $"{ConversationUrl(tenantId, conversationId)}/messages";

    public static string SearchUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/messaging/messages/search";

    public static string MediaUrl(Guid tenantId, Guid messageId) => $"/api/v1/tenants/{tenantId}/messaging/media/{messageId}";

    private const string TemplateDatabase = "qep_template_messaging";

    // Un solo contenedor por ensamblado, con una plantilla ya migrada: cada prueba clona la plantilla
    // (CREATE DATABASE … TEMPLATE) y su host sólo comprueba que no hay migraciones pendientes.
    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    // Postgres rechaza dos CREATE DATABASE simultáneos desde la misma plantilla: los clones van de a uno.
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

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
        // Sin el token de una prueba: el servidor es de todas.
        var server = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            // Con la suite completa xUnit corre las clases en paralelo, cada una con su base, su host y su
            // pool de Npgsql, más los 64 INSERT concurrentes de la prueba de carga y los workers: el límite
            // por defecto (100) se agota y la API responde 500 con 53300 (too many clients).
            .WithCommand("-c", "max_connections=400")
            .Build();
        await server.StartAsync(CancellationToken.None);
        await ExecuteAdminAsync(server, $"CREATE DATABASE \"{TemplateDatabase}\"");

        // El host migra al arrancar: arrancarlo una vez contra la plantilla la deja con todo.
        var templateConnectionString = ConnectionStringFor(server, TemplateDatabase);
        using (var factory = new QepApiFactory(templateConnectionString))
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
        // Contra la base "qep" del contenedor, nunca contra la plantilla ni contra la de una prueba.
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
            // FORCE: un worker del host que todavía no soltó su conexión no puede dejar la base viva.
            await ExecuteAdminAsync(server, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    public sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        /// <summary>Lo que sale hacia Graph —desde Integrations y desde Messaging— lo ve este handler.</summary>
        public FakeMetaGraphHandler MetaHandler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Pool acotado sólo en pruebas: un host no puede acaparar las conexiones del contenedor compartido.
            builder.UseSetting("ConnectionStrings:QepDatabase", new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 80 }.ConnectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijados, nunca heredados (mismo criterio que IntegrationsApiHarness).
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
            builder.UseSetting("Integrations:SecretProtection:ActiveKeyId", "test");
            builder.UseSetting("Integrations:SecretProtection:Keys:test", TestSecretProtectionKey);
            builder.UseSetting("Integrations:SecretProtection:Keys:k1", string.Empty);
            builder.UseSetting("Integrations:Zenvia:BaseUrl", "https://zenvia.test");
            // Spec 2026-10-09 §9: las cinco de Meta:App, con centinelas.
            builder.UseSetting("Meta:App:AppId", MetaAppId);
            builder.UseSetting("Meta:App:ConfigId", MetaConfigId);
            builder.UseSetting("Meta:App:GraphApiVersion", "v24.0");
            builder.UseSetting("Meta:App:AppSecret", TestAppSecret);
            builder.UseSetting("Meta:App:WebhookVerifyToken", TestVerifyToken);
            // Los workers no corren solos durante una prueba: la prueba llama DrainAsync.
            builder.UseSetting("Messaging:Workers:DeliveryPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:MediaPollSeconds", "3600");
            builder.UseSetting("Messaging:Workers:PurgeIntervalHours", "24");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(IntegrationsGraphClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => MetaHandler);
                services.AddHttpClient(MessagingGraphClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => MetaHandler);
            });
        }
    }

    internal sealed record RegisteredTenant(Guid TenantId, Guid OwnerUserId, string Email);

    public static HttpClient CreateClient(
        WebApplicationFactory<Program> factory, Guid subjectId, Guid tenantId, params string[] permissions)
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

    /// <summary>Copia de <c>PosApiHarness.RegisterTenantAsync</c>: un tenant real, con el dueño y su
    /// membresía activa.</summary>
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
                displayName = "Messaging Test Org",
                slug = $"org-{Guid.NewGuid():N}"[..12],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync<RegisterTenantResponseDto>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);
        return new RegisteredTenant(registered.TenantId, registered.OwnerUserId, email);
    }

    public static Task<Guid> OwnerMembershipIdAsync(string connectionString, RegisteredTenant tenant) =>
        ScalarAsync<Guid>(
            connectionString,
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @tenantId AND user_id = @userId",
            ("tenantId", tenant.TenantId),
            ("userId", tenant.OwnerUserId));

    /// <summary><c>messaging</c> no viene con el signup (spec §6.2): se prende con su fila, como
    /// <c>PosApiHarness.EnablePosAsync</c>.</summary>
    public static Task<int> EnableMessagingAsync(string connectionString, Guid tenantId) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
            VALUES (@tenantId, 'messaging', now(), 'manual')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'active', status_changed_at = now()
            """,
            ("tenantId", tenantId));

    /// <summary>Una conexión whatsapp-cloud con su ruta, escrita directo por el repositorio de Integrations
    /// (sin Meta), cifrada con la llave activa del host.</summary>
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

    /// <summary>Una conversación abierta y sin mensajes, escrita por el <c>MessagingDbContext</c> del host.</summary>
    public static async Task<Guid> SeedConversationAsync(
        WebApplicationFactory<Program> host, Guid tenantId, Guid connectionId, string waId, string? profileName = "Laura")
    {
        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
        var conversation = Conversation.Start(Guid.CreateVersion7(), tenantId, connectionId, waId, profileName, DateTimeOffset.UtcNow);
        dbContext.Conversations.Add(conversation);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return conversation.Id;
    }

    /// <summary>Spec 2026-10-10: una conversación con BSUID (y teléfono si se pasa), con la ventana abierta si se pasa
    /// <paramref name="lastInboundAt"/>, como la dejaría la ingesta.</summary>
    public static async Task<Guid> SeedBsuidConversationAsync(
        WebApplicationFactory<Program> host, string connectionString, Guid tenantId, Guid connectionId, string userId, string? waId, DateTimeOffset? lastInboundAt = null)
    {
        Guid id;
        using (var scope = host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MessagingDbContext>();
            var conversation = Conversation.StartWithUserId(Guid.CreateVersion7(), tenantId, connectionId, userId, waId, "Laura", DateTimeOffset.UtcNow);
            dbContext.Conversations.Add(conversation);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            id = conversation.Id;
        }

        if (lastInboundAt is { } at)
        {
            await ExecuteAsync(connectionString,
                "UPDATE messaging.conversations SET last_inbound_at = @at, last_inbound_wamid = 'wamid.seed' WHERE id = @id",
                ("at", at.ToUniversalTime()), ("id", id));
        }

        return id;
    }

    /// <summary>Un cliente de QEP creado por HTTP, con el cuerpo mínimo de <c>CustomersApiHarness.NewCustomerBody</c>
    /// (colombiano, ciudad DIVIPOLA real y una clasificación nueva). Customers calcula <c>phone_e164</c> al
    /// crear, que es lo que Messaging empareja (spec 2026-10-09 §6.5). Devuelve el <c>id</c>.</summary>
    public static async Task<Guid> CreateCustomerAsync(
        WebApplicationFactory<Program> factory, RegisteredTenant tenant, string name, string phone)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = CreateClient(
            factory, tenant.OwnerUserId, tenant.TenantId,
            "customers.customer.read", "customers.customer.manage", "customers.classification.read", "customers.classification.manage");

        var departments = await client.GetFromJsonAsync<List<GeographyItemDto>>("/api/v1/departments", ct);
        Assert.NotNull(departments);
        Guid? cityId = null;
        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyItemDto>>($"/api/v1/cities?departmentId={department.Id}", ct);
            if (cities is { Count: > 0 })
            {
                cityId = cities[0].Id;
                break;
            }
        }

        Assert.NotNull(cityId);
        var customersUrl = $"/api/v1/tenants/{tenant.TenantId}/customers";
        var classificationResponse = await client.PostAsJsonAsync($"{customersUrl}/classifications", new { name = "Mediano", prefix = "CLI" }, ct);
        classificationResponse.EnsureSuccessStatusCode();
        var classification = await classificationResponse.Content.ReadFromJsonAsync<IdDto>(ct);
        Assert.NotNull(classification);

        var response = await client.PostAsJsonAsync(
            customersUrl,
            new
            {
                name,
                identificationType = "NIT",
                identificationNumber = "900.123.456-1",
                phone,
                email = "compras@verde.co",
                address = "Calle 10 # 45-12",
                country = "CO",
                cityId,
                classificationId = classification.Id,
                withRetention = false,
                vatSurplus = false,
            },
            ct);
        response.EnsureSuccessStatusCode();
        var customer = await response.Content.ReadFromJsonAsync<IdDto>(ct);
        Assert.NotNull(customer);
        return customer.Id;
    }

    private sealed record GeographyItemDto(Guid Id);

    private sealed record IdDto(Guid Id);

    /// <summary>La firma que Meta pone en <c>X-Hub-Signature-256</c>: HMAC-SHA256 del cuerpo con el AppSecret.</summary>
    public static string Sign(byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestAppSecret), body));

    /// <summary>Sin <c>X-Qep-Client</c>: el webhook está exento de CSRF por ruta.</summary>
    public static async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string json, string? signature = null)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookUrl);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature ?? Sign(bytes));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, object? body = null, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("X-Qep-Client", "web");
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>El <c>code</c> y las claves de <c>errors</c> de un ProblemDetails.</summary>
    public static async Task<(string? Code, string[] ErrorKeys)> ProblemAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
        var keys = root.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        return (code, keys);
    }

    /// <summary>Corre una pasada del worker de entregas (P10): reclama y procesa lo pendiente.</summary>
    public static async Task DrainDeliveriesAsync(WebApplicationFactory<Program> host)
    {
        var worker = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<Modules.Messaging.Infrastructure.Webhook.WebhookDeliveryWorker>().Single();
        await worker.DrainAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Corre una pasada de la copia de medios (§8.6, P10): reclama y copia lo pendiente.</summary>
    public static async Task DrainMediaAsync(WebApplicationFactory<Program> host)
    {
        var worker = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<Modules.Messaging.Infrastructure.Media.MediaCopyWorker>().Single();
        await worker.DrainAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Un saliente ya guardado, como lo deja el envío (§8.3): Sent con wamid, o Failed -1 sin wamid.</summary>
    public static async Task<Guid> SeedOutboundAsync(
        string connectionString, Guid conversationId, Guid tenantId, Guid connectionId, string? wamid, short status = 1, int? failureCode = null, long occurredAtUnix = 1760000000)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, wamid, client_id, failure_code, created_at)
            VALUES (@id, @conversationId, @tenantId, @connectionId, to_timestamp(@occurredAt), 2, 1, @status, 'respuesta', @wamid, @clientId, @failureCode, now())
            """,
            ("id", id), ("conversationId", conversationId), ("tenantId", tenantId), ("connectionId", connectionId),
            ("occurredAt", occurredAtUnix), ("status", status), ("wamid", (object?)wamid ?? DBNull.Value), ("clientId", Guid.CreateVersion7()),
            ("failureCode", (object?)failureCode ?? DBNull.Value));
        await ExecuteAsync(connectionString,
            "UPDATE messaging.conversations SET last_message_id = @id, last_message_direction = 2, last_message_kind = 1, last_message_status = @status, last_message_at = to_timestamp(@occurredAt), last_activity_at = to_timestamp(@occurredAt) WHERE id = @conversationId",
            ("id", id), ("status", status), ("occurredAt", occurredAtUnix), ("conversationId", conversationId));
        return id;
    }

    /// <summary>Corre una pasada de la purga de entregas (P10).</summary>
    public static async Task DrainPurgeAsync(WebApplicationFactory<Program> host)
    {
        var worker = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<Modules.Messaging.Infrastructure.Webhook.WebhookPurgeWorker>().Single();
        await worker.DrainAsync(TestContext.Current.CancellationToken);
    }

    public static Task<long> CountAsync(string connectionString, string sql, params (string Name, object Value)[] parameters) =>
        ScalarAsync<long>(connectionString, sql, parameters);

    public static async Task<T> ScalarAsync<T>(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
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

    public static async Task<int> ExecuteAsync(
        string connectionString, string sql, params (string Name, object Value)[] parameters)
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

    /// <summary>Un proveedor de logs más: LoggerFactory recibe todos los ILoggerProvider registrados.</summary>
    public static WebApplicationFactory<Program> WithCapturedLogs(
        this WebApplicationFactory<Program> factory, CapturedLogs logs) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<ILoggerProvider>(logs)));

    /// <summary>P10: un reloj que la prueba mueve. <c>IClock</c> es scoped en <c>AddQepPlatform</c>, así que el
    /// reemplazo va por <c>ConfigureTestServices</c>, que corre después del registro del host.</summary>
    public static WebApplicationFactory<Program> WithTestClock(
        this WebApplicationFactory<Program> factory, TestClock clock) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        }));

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);
}

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }
}

/// <summary>Todo lo que se registra, ya formateado y con la excepción entera.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly ConcurrentQueue<string> _categories = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public IReadOnlyCollection<string> Categories => _categories;

    public string AllText => string.Join('\n', _entries);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries, _categories);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        string categoryName, ConcurrentQueue<string> entries, ConcurrentQueue<string> categories) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            categories.Enqueue(categoryName);
            entries.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : "\n" + exception));
        }
    }
}
