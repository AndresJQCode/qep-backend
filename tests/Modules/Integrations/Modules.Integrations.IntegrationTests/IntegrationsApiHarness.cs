using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Zenvia;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Integrations.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de Integrations (spec 2026-10-08). Viene de <c>WhatsAppTestHarness</c>
/// (6612298): centinela, Zenvia de mentira, logs capturados y llaves fijadas. El host fija todo lo que
/// activa una integración externa o un secreto; nunca lo hereda de los user-secrets de quien corre las
/// pruebas (memoria «user-secrets rompen las pruebas de integración»).
/// </summary>
internal static class IntegrationsApiHarness
{
    /// <summary>Un token inventado y fácil de buscar: la prueba de fugas lo persigue por respuestas,
    /// logs, auditoría, outbox y <c>platform.request_failures</c>. ASCII visible, sin espacios.</summary>
    public const string SentinelApiToken = "zenvia-token-SENTINEL-7f3a9c";

    /// <summary>Un cuerpo de Zenvia inventado: no puede aparecer en ningún camino de salida.</summary>
    public const string SentinelZenviaBody = "zenvia-body-SENTINEL-5b2d1e";

    public const string MetaAppId = "100200300";

    public const string MetaConfigId = "400500600";

    /// <summary>Centinelas: la prueba de fugas los persigue igual que al token de Zenvia.</summary>
    public const string SentinelMetaAppSecret = "meta-app-secret-SENTINEL-9e8d7c";

    public const string SentinelMetaVerifyToken = "meta-verify-token-SENTINEL-6b5a4f-0123456789";

    public const string FromNumber = "573001234567";

    public const string ZenviaBaseUrl = "https://zenvia.test";

    /// <summary>La llave con la que el host cifra. Calculada, no escrita: un literal con forma de llave
    /// en el repo termina copiado a un ambiente. Bytes 0..31.</summary>
    public static string TestSecretProtectionKey { get; } =
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(index => (byte)index).ToArray());

    public static readonly string[] ReadPermissions = [IntegrationsPermissions.ConnectionRead];

    public static readonly string[] ManagePermissions =
        [IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage];

    public static string CatalogUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/integrations/catalog";

    public static string ConnectionsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/integrations/connections";

    public static string ConnectionUrl(Guid tenantId, Guid connectionId) => $"{ConnectionsUrl(tenantId)}/{connectionId}";

    private const string TemplateDatabase = "qep_template";

    // Un solo contenedor por ensamblado, con una plantilla ya migrada. Antes cada prueba arrancaba su
    // contenedor y su host migraba los 13 módulos —por eso Quotations tarda 15 minutos—; ahora cada
    // prueba clona la plantilla (CREATE DATABASE … TEMPLATE, decenas de milisegundos) y el arranque
    // de su host sólo comprueba que no hay migraciones pendientes. El aislamiento es el mismo: una
    // base por prueba, que se borra al terminar.
    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    // Postgres rechaza dos CREATE DATABASE simultáneos desde la misma plantilla ("source database
    // is being accessed by other users"): los clones van de a uno.
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
        // Sin el token de una prueba: el servidor es de todas, y cancelar la primera no puede
        // dejar a las demás sin base.
        var server = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await server.StartAsync(CancellationToken.None);
        await ExecuteAdminAsync(server, $"CREATE DATABASE \"{TemplateDatabase}\"");

        // El host migra al arrancar (los *DatabaseInitializer de cada módulo): arrancarlo una vez
        // contra la plantilla la deja con todas las migraciones, igual que cada prueba la tenía antes.
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

    /// <summary>La base de una prueba. Expone lo mismo que las pruebas le pedían al contenedor.</summary>
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
        /// <summary>Lo que sale hacia Zenvia lo ve este handler; las pruebas cambian su respuesta.</summary>
        public FakeZenviaHandler ZenviaHandler { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijados, nunca heredados (mismo criterio que PosApiHarness y QuotationsApiHarness).
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:ExportLoad:Quotations", "0");
            builder.UseSetting("Quotations:WhatsApp:ApiToken", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:FromNumber", string.Empty);
            builder.UseSetting("Quotations:WhatsApp:TemplateId", string.Empty);
            // El signup concede los seis módulos de fábrica, quotations incluido: Zenvia es visible.
            builder.UseSetting("Entitlements:GrantDefaultModulesOnSignup", "true");
            // Spec 2026-10-08, «Pruebas»: "test" es la activa; "k1" —el id que el README sugiere para
            // local— se vacía para que una llave mal pegada en la máquina de quien corre no las tumbe.
            builder.UseSetting("Integrations:SecretProtection:ActiveKeyId", "test");
            builder.UseSetting("Integrations:SecretProtection:Keys:test", TestSecretProtectionKey);
            builder.UseSetting("Integrations:SecretProtection:Keys:k1", string.Empty);
            builder.UseSetting("Integrations:Zenvia:BaseUrl", ZenviaBaseUrl);
            // Spec 2026-10-09 §9: fijadas, nunca heredadas. Con las cinco, whatsapp-cloud es visible
            // (D-M3); las pruebas que quieren el caso contrario las vacían con WithoutMetaApp.
            builder.UseSetting("Meta:App:AppId", MetaAppId);
            builder.UseSetting("Meta:App:ConfigId", MetaConfigId);
            builder.UseSetting("Meta:App:GraphApiVersion", "v24.0");
            builder.UseSetting("Meta:App:AppSecret", SentinelMetaAppSecret);
            builder.UseSetting("Meta:App:WebhookVerifyToken", SentinelMetaVerifyToken);
            builder.ConfigureTestServices(services => services
                .AddHttpClient(ZenviaConnectionTester.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => ZenviaHandler));
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
    /// membresía activa (de ahí sale <c>createdBy</c>).</summary>
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
                displayName = "Integrations Test Org",
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

    /// <summary>Objeto anónimo a propósito: así una prueba manda exactamente lo que quiere.</summary>
    public static object ZenviaBody(
        string name = "WhatsApp sede norte", string? apiToken = SentinelApiToken, string? fromNumber = FromNumber) =>
        new
        {
            providerKey = "zenvia",
            name,
            fields = new Dictionary<string, string?> { ["fromNumber"] = fromNumber },
            secrets = new Dictionary<string, string?> { ["apiToken"] = apiToken },
        };

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

    public static async Task<ConnectionResponse> CreateConnectionAsync(
        HttpClient client, Guid tenantId, string name = "WhatsApp sede norte", string apiToken = SentinelApiToken)
    {
        var response = await SendAsync(client, HttpMethod.Post, ConnectionsUrl(tenantId), ZenviaBody(name, apiToken));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(connection);
        return connection;
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

    /// <summary>Spec 2026-10-08 (consola): apagar un módulo deja la fila inactiva.</summary>
    public static Task<int> SetModuleStatusAsync(string connectionString, Guid tenantId, string moduleKey, string status) =>
        ExecuteAsync(
            connectionString,
            "UPDATE tenancy.tenant_modules SET status = @status, status_changed_at = now() WHERE tenant_id = @tenantId AND module_key = @moduleKey",
            ("status", status),
            ("tenantId", tenantId),
            ("moduleKey", moduleKey));

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

    public static Task<long> CountConnectionsAsync(string connectionString, Guid tenantId) =>
        ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM integrations.connections WHERE tenant_id = @tenantId",
            ("tenantId", tenantId));

    /// <summary>Una conexión de Zenvia escrita directo por el repositorio del host (sin HTTP ni prueba
    /// contra el proveedor), cifrada con la llave activa de ese host.</summary>
    public static async Task<Guid> SeedConnectionAsync(
        WebApplicationFactory<Program> host, Guid tenantId, string name, string apiToken = SentinelApiToken)
    {
        using var scope = host.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var connection = IntegrationConnection.Create(
            IntegrationProviders.Zenvia,
            tenantId,
            name,
            new Dictionary<string, string> { [ZenviaFieldKeys.FromNumber] = FromNumber },
            new Dictionary<string, string> { [ZenviaFieldKeys.ApiToken] = apiToken },
            protector.Protect,
            Guid.CreateVersion7(),
            DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().Add(connection);
        await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
        return connection.Id;
    }

    /// <summary>Un proveedor de logs más: LoggerFactory recibe todos los ILoggerProvider registrados,
    /// así que esto ve lo mismo que la consola.</summary>
    public static WebApplicationFactory<Program> WithCapturedLogs(
        this WebApplicationFactory<Program> factory, CapturedLogs logs) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<ILoggerProvider>(logs)));

    /// <summary>Pisa la llave activa y declara las que se pasen. Se aplica después del ConfigureWebHost
    /// del harness, así que gana.</summary>
    public static WebApplicationFactory<Program> WithSecretProtection(
        this WebApplicationFactory<Program> factory, string activeKeyId, params (string Id, string Value)[] keys) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Integrations:SecretProtection:ActiveKeyId", activeKeyId);
            foreach (var (id, value) in keys)
            {
                builder.UseSetting($"Integrations:SecretProtection:Keys:{id}", value);
            }
        });

    private static readonly string[] MetaAppRequiredKeys = ["AppId", "ConfigId", "AppSecret", "WebhookVerifyToken"];

    /// <summary>Vacía la sección Meta:App (D-M3): whatsapp-cloud desaparece del catálogo.</summary>
    public static WebApplicationFactory<Program> WithoutMetaApp(this WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(builder =>
        {
            foreach (var key in MetaAppRequiredKeys)
            {
                builder.UseSetting($"Meta:App:{key}", string.Empty);
            }
        });

    /// <summary>Mensaje y detalle de todas las fallas guardadas: lo que lee la pantalla de Log.</summary>
    public static Task<string> RequestFailuresTextAsync(string connectionString) =>
        ScalarAsync<string>(
            connectionString,
            "SELECT coalesce(string_agg(message || ' ' || detail, ' '), '') FROM platform.request_failures");

    /// <summary>Todo lo que Integrations dejó en auditoría y outbox, como texto.</summary>
    public static Task<string> AuditAndOutboxTextAsync(string connectionString) =>
        ScalarAsync<string>(
            connectionString,
            """
            SELECT coalesce((SELECT string_agg(action || ' ' || resource_id || ' ' || changed_fields::text, ' ')
                             FROM audit.entries WHERE source = 'integrations'), '')
                || ' '
                || coalesce((SELECT string_agg(payload::text, ' ')
                             FROM platform.outbox_messages WHERE event_name LIKE 'integrations.%'), '')
            """);

    private sealed record RegisterTenantResponseDto(Guid TenantId, Guid OwnerUserId);
}

internal sealed record ZenviaRequest(HttpMethod Method, Uri? Uri, string? Token, string UserAgent);

/// <summary>Zenvia de mentira: anota cada request y responde lo que la prueba pida. Lo comparte el host
/// entero; <see cref="HttpMessageHandler"/> no guarda estado al desecharse, así que sobrevive a que
/// IHttpClientFactory recicle su cadena.</summary>
internal sealed class FakeZenviaHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = "[]";

    public Exception? Throw { get; set; }

    public ConcurrentQueue<ZenviaRequest> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(new ZenviaRequest(
            request.Method,
            request.RequestUri,
            request.Headers.TryGetValues("X-API-TOKEN", out var tokens) ? tokens.FirstOrDefault() : null,
            request.Headers.UserAgent.ToString()));
        if (Throw is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
}

/// <summary>Todo lo que se registra, ya formateado y con la excepción entera (mensaje, internas y
/// pila): es lo mismo que guarda el log JSON de producción.</summary>
internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();
    private readonly ConcurrentQueue<string> _categories = new();

    public IReadOnlyCollection<string> Entries => _entries;

    /// <summary>Categoría de cada entrada registrada (una por entrada, con repetidas): sirve para probar
    /// qué componente NO está logueando, p. ej. los handlers de logging de IHttpClientFactory.</summary>
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
