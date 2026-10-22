using System.Net;
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
using Microsoft.Extensions.Hosting;
using Modules.Automations.Api;
using Modules.Automations.Application;
using Modules.Automations.Infrastructure.Engine;
using Modules.Automations.Infrastructure.Http;
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

    /// <summary>Los de <c>MessagingApiHarness</c>: el secreto firma el webhook de WhatsApp (<see cref="PostWebhookAsync"/>).</summary>
    public const string TestAppSecret = "meta-app-secret-SENTINEL-m1";

    public const string TestVerifyToken = "meta-verify-token-SENTINEL-m2-0123456789abcdef";

    public const string MetaAppId = "100200300";

    public const string MetaConfigId = "400500600";

    public const string WebhookUrl = "/api/webhooks/whatsapp";

    /// <summary>Los clientes de Graph de Integrations y de Mensajería (sus nombres son internal de cada módulo), que el host apunta a
    /// <see cref="QepApiFactory.MetaHandler"/>: ninguna prueba sale a Meta.</summary>
    private static readonly string[] GraphClientNames =
    [
        "integrations.meta-graph", "messaging.meta-graph", "messaging.meta-graph-upload", "messaging.meta-graph-media",
        "messaging.instagram-graph", "messaging.instagram-download", "integrations.instagram-graph",
    ];

    /// <summary><c>ExternalApiConnectionTester.HttpClientName</c>, que es interno de Integrations.Infrastructure.</summary>
    private const string ExternalApiClientName = "integrations.external-api";

    /// <summary>Una API externa de mentira que acepta cualquier credencial.</summary>
    public sealed class AcceptingExternalApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

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

    /// <summary>
    /// Las conexiones «API externa» que ve el host. Las pruebas del Anexo A necesitan los ids fijos de
    /// <see cref="AnnexA.CustomersApi"/> y <see cref="AnnexA.ServiceOrdersApi"/>, que una conexión creada por la API no puede tener: le
    /// pasan este doble a <see cref="QepApiFactory"/>. Las que prueban la lectura real usan <see cref="SeedExternalApiConnectionAsync"/>.
    /// </summary>
    public sealed class FakeExternalApis : IAutomationExternalApis
    {
        public List<ExternalApiRef> Items { get; } = [];

        /// <summary>Las dos del Anexo A, activas.</summary>
        public static FakeExternalApis WithAnnexA() =>
            new() { Items = { new ExternalApiRef(AnnexA.CustomersApi, "Clientes", "Active"), new ExternalApiRef(AnnexA.ServiceOrdersApi, "Órdenes", "Active") } };

        public Task<IReadOnlyList<ExternalApiRef>> ListAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExternalApiRef>>([.. Items]);

        /// <summary>Las del Anexo A sólo se listan: ninguna de estas pruebas llama a la API (eso lo prueba la tarea 12 con una conexión real).</summary>
        public Task<ExternalApiCredentials?> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<ExternalApiCredentials?>(null);
    }

    /// <summary>Un reloj que la prueba mueve. Copia de <c>TestClock</c> de <c>MessagingApiHarness</c>: arranca en el instante real para que
    /// lo que depende de «ahora» (membresías, vencimientos) no se descuadre, y la prueba lo adelanta con <see cref="UtcNow"/>.</summary>
    public sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <param name="enqueueAutomation">Spec 2026-10-21 D-M1: <c>Messaging:Automation:Enqueue</c>, fijado siempre (nunca heredado);
    /// prendido por defecto como en producción desde que el motor existe.</param>
    public sealed class QepApiFactory(
        string connectionString, FakeExternalApis? externalApis = null, TestClock? clock = null, bool enqueueAutomation = true)
        : WebApplicationFactory<Program>
    {
        /// <summary>Lo que sale hacia Graph —desde Integrations y desde Mensajería— lo ve este handler.</summary>
        public FakeMetaGraphHandler MetaHandler { get; } = new();

        /// <summary>La API de las conexiones external-api que siembra <see cref="SeedExternalApiConnectionAsync"/>: responde 200, así el
        /// probador da la credencial por buena sin salir a la red.</summary>
        public AcceptingExternalApiHandler ExternalApiHandler { get; } = new();

        /// <summary>Spec 2026-10-21 D-H4: la API que llama el bloque HTTP del motor (<see cref="HttpBlockExecutor.HttpClientName"/>). Reemplaza
        /// el handler anti-SSRF sólo en este host: la URL base de las conexiones sembradas no resuelve en ningún DNS.</summary>
        public FakeExternalApiHandler ExternalApi { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                foreach (var name in GraphClientNames)
                {
                    services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => MetaHandler);
                }

                // Spec 2026-10-21 D-H3: el probador real de external-api corre al crear la conexión; su API la responde este doble.
                services.AddHttpClient(ExternalApiClientName).ConfigurePrimaryHttpMessageHandler(() => ExternalApiHandler);
                services.AddHttpClient(HttpBlockExecutor.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ExternalApi);
            });

            if (clock is not null)
            {
                // IClock es scoped en AddQepPlatform: se reemplaza por la instancia de la prueba.
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IClock>();
                    services.AddSingleton<IClock>(clock);
                });
            }

            if (externalApis is not null)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IAutomationExternalApis>();
                    services.AddSingleton<IAutomationExternalApis>(externalApis);
                });
            }

            builder.UseEnvironment("Development");
            // Sin vigilar archivos de configuración (MessagingApiHarness explica el límite de FSEvents de macOS).
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
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
            // Spec 2026-10-09 §9: las de Meta:App con los centinelas de MessagingApiHarness; el secreto valida la firma del webhook.
            builder.UseSetting("Meta:App:AppId", MetaAppId);
            builder.UseSetting("Meta:App:ConfigId", MetaConfigId);
            builder.UseSetting("Meta:App:GraphApiVersion", "v24.0");
            builder.UseSetting("Meta:App:AppSecret", TestAppSecret);
            builder.UseSetting("Meta:App:WebhookVerifyToken", TestVerifyToken);
            builder.UseSetting("Messaging:Automation:Enqueue", enqueueAutomation ? "true" : "false");
            // El motor tampoco corre solo: la prueba llama DrainAutomationsAsync.
            builder.UseSetting("Automations:Workers:InboxPollSeconds", "3600");
            builder.UseSetting("Automations:Workers:AbandonSweepMinutes", "1440");
            builder.UseSetting("Automations:Workers:SimulationSweepMinutes", "1440");
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

    /// <summary>Spec 2026-10-21 D-H1: una conexión «API externa» del tenant, creada por <c>POST /integrations/connections</c> como la crea la pantalla
    /// de Integrations (no hay otro camino: es un proveedor por formulario). <paramref name="status"/> es <c>Active</c> o <c>Paused</c>; la segunda
    /// la pausa por <c>POST …/pause</c>. El tenant tiene que tener <c>automations</c> prendido (<see cref="EnableAutomationsAsync"/>).</summary>
    public static async Task<Guid> SeedExternalApiConnectionAsync(
        WebApplicationFactory<Program> host, Guid tenantId, Guid ownerUserId, string name, string status,
        string baseUrl = "https://api-clasic.qcode.co", string apiKey = "external-api-key-SENTINEL-a9")
    {
        using var client = CreateClient(
            host, ownerUserId, tenantId, IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage);
        var url = $"/api/v1/tenants/{tenantId}/integrations/connections";
        var created = await SendMutationAsync(client, url, new
        {
            providerKey = IntegrationProviders.ExternalApi.Key,
            name,
            fields = new Dictionary<string, string?> { ["baseUrl"] = baseUrl, ["authHeader"] = "x-api-key" },
            secrets = new Dictionary<string, string?> { ["apiKey"] = apiKey },
        }, ifMatch: null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
        if (status == "Paused")
        {
            var paused = await SendMutationAsync(client, $"{url}/{id}/pause", body: null, ifMatch: "\"1\"");
            Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        }
        else
        {
            Assert.Equal("Active", status);
        }

        return id;
    }

    private static async Task<HttpResponseMessage> SendMutationAsync(HttpClient client, string url, object? body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
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

    /// <summary>La firma que Meta pone en <c>X-Hub-Signature-256</c>: HMAC-SHA256 del cuerpo con el AppSecret.</summary>
    public static string Sign(byte[] body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(TestAppSecret), body));

    /// <summary>Sin <c>X-Qep-Client</c>: el webhook está exento de CSRF por ruta. Copia de <c>MessagingApiHarness.PostWebhookAsync</c>.</summary>
    public static async Task<HttpResponseMessage> PostWebhookAsync(HttpClient client, string json, string? signature = null)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var request = new HttpRequestMessage(HttpMethod.Post, WebhookUrl);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature ?? Sign(bytes));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Una pasada del worker de entregas de Mensajería: la ingesta del webhook, que llena <c>automation_inbox</c>.</summary>
    public static Task DrainDeliveriesAsync(WebApplicationFactory<Program> host) =>
        HostedService<Modules.Messaging.Infrastructure.Webhook.WebhookDeliveryWorker>(host).DrainAsync(TestContext.Current.CancellationToken);

    /// <summary>Una pasada de la copia de medios de Mensajería (§8.6).</summary>
    public static Task DrainMediaAsync(WebApplicationFactory<Program> host) =>
        HostedService<Modules.Messaging.Infrastructure.Media.MediaCopyWorker>(host).DrainAsync(TestContext.Current.CancellationToken);

    /// <summary>Spec 2026-10-21 §5.2: una pasada del motor (reclama y procesa lo pendiente del inbox).</summary>
    public static Task DrainAutomationsAsync(WebApplicationFactory<Program> host) =>
        HostedService<AutomationInboxWorker>(host).DrainAsync(TestContext.Current.CancellationToken);

    /// <summary>D-M13 y P2-R7: un barrido del motor con la hora que dice la prueba (abandona las corridas en espera y purga el inbox).</summary>
    public static Task SweepAutomationsAsync(WebApplicationFactory<Program> host, DateTimeOffset now) =>
        HostedService<AutomationInboxWorker>(host).SweepAsync(now, TestContext.Current.CancellationToken);

    /// <summary>Spec 2026-10-21 D-S1: un barrido de las simulaciones vencidas con la hora que dice la prueba.</summary>
    public static Task SweepSimulationsAsync(WebApplicationFactory<Program> host, DateTimeOffset now) =>
        HostedService<AutomationInboxWorker>(host).SweepSimulationsAsync(now, TestContext.Current.CancellationToken);

    private static T HostedService<T>(WebApplicationFactory<Program> host) =>
        host.Services.GetServices<IHostedService>().OfType<T>().Single();

    /// <summary>Responder manda el acuse de leído por la misma ruta de Graph que el envío; las pruebas que cuentan envíos lo descartan.
    /// Copia de <c>MessagingApiHarness.IsReadReceipt</c>.</summary>
    public static bool IsReadReceipt(string? body) =>
        body is not null
        && (body.Contains("\"status\":\"read\"", StringComparison.Ordinal) || body.Contains("\"sender_action\":\"mark_seen\"", StringComparison.Ordinal));

    /// <summary>Un chatbot publicado por la API, como lo haría el admin: crear en blanco con la conexión, guardar el flujo y publicar.</summary>
    public static async Task<Guid> PublishChatbotAsync(
        HttpClient client, Guid tenantId, string flowJson, Guid connectionId, string trigger = "NewConversation", params string[] keywords)
    {
        var ct = TestContext.Current.CancellationToken;
        var chatbots = $"{AutomationsUrl(tenantId)}/chatbots";
        var created = await client.PostAsJsonAsync(
            chatbots,
            new CreateChatbotRequest("Servicio", "Atiende la línea de servicio.", trigger, keywords, [connectionId], new ChatbotSourceRequest("blank", null, null, null)),
            ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var editor = (await created.Content.ReadFromJsonAsync<ChatbotEditorDto>(ct))!;

        using var draft = new HttpRequestMessage(HttpMethod.Put, $"{chatbots}/{editor.Chatbot.Id}/draft")
        {
            Content = JsonContent.Create(new SaveDraftRequest(JsonDocument.Parse(flowJson).RootElement, JsonDocument.Parse("{}").RootElement)),
        };
        draft.Headers.TryAddWithoutValidation("If-Match", $"\"{editor.Chatbot.Version}\"");
        var saved = await client.SendAsync(draft, ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var version = (await saved.Content.ReadFromJsonAsync<DraftSavedDto>(ct))!.Version;

        using var publish = new HttpRequestMessage(HttpMethod.Post, $"{chatbots}/{editor.Chatbot.Id}/publish");
        publish.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        var published = await client.SendAsync(publish, ct);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return editor.Chatbot.Id;
    }

    /// <summary>D-C9 y D-C15: un chatbot importado por <c>POST /chatbots/import</c> desde el flujo <paramref name="flowJson"/> y su
    /// <paramref name="layoutJson"/> (el <c>Layout</c> del archivo exportado), con el mapa de conexiones «API externa» del archivo a las del
    /// tenant. Queda en <c>Draft</c> y sin conexiones, como lo deja importar.</summary>
    public static async Task<ChatbotEditorDto> ImportChatbotAsync(
        HttpClient client, Guid tenantId, string flowJson, string layoutJson, IReadOnlyDictionary<Guid, Guid> connectionMap, string name = "Clasic")
    {
        var ct = TestContext.Current.CancellationToken;
        var export = new ChatbotExportDto(
            ChatbotExportDto.CurrentSchema, name, "Servicio técnico de calentadores.", "NewConversation", [], "reiniciar", 3,
            JsonDocument.Parse(flowJson).RootElement, JsonDocument.Parse(layoutJson).RootElement,
            [.. connectionMap.Keys.Select(id => new ExternalApiRefDto(id, "API de Clasic", "Active"))], DateTimeOffset.UtcNow);
        var imported = await client.PostAsJsonAsync($"{AutomationsUrl(tenantId)}/chatbots/import", new ImportChatbotRequest(null, export, connectionMap), ct);
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        return (await imported.Content.ReadFromJsonAsync<ChatbotEditorDto>(ct))!;
    }

    /// <summary>Lo que hace el admin después de importar: le asigna la conexión de WhatsApp (<c>PUT /chatbots/{id}</c>, con los ajustes que
    /// trajo el archivo) y lo publica, cada paso con su <c>If-Match</c>.</summary>
    public static async Task PublishImportedAsync(HttpClient client, Guid tenantId, ChatbotEditorDto imported, Guid connectionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var url = $"{AutomationsUrl(tenantId)}/chatbots/{imported.Chatbot.Id}";
        using var update = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(new UpdateChatbotRequest(
                imported.Chatbot.Name, imported.Chatbot.Description, imported.Chatbot.Trigger, imported.Keywords, imported.RestartKeyword,
                imported.MaxAttempts, [connectionId])),
        };
        update.Headers.TryAddWithoutValidation("If-Match", $"\"{imported.Chatbot.Version}\"");
        var updated = await client.SendAsync(update, ct);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var version = (await updated.Content.ReadFromJsonAsync<ChatbotEditorDto>(ct))!.Chatbot.Version;

        using var publish = new HttpRequestMessage(HttpMethod.Post, $"{url}/publish");
        publish.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        var published = await client.SendAsync(publish, ct);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
    }

    /// <summary>Una versión nueva de un chatbot ya publicado (D-C1): guardar otro borrador y publicar, con el <c>If-Match</c> de cada paso.</summary>
    public static async Task RepublishChatbotAsync(HttpClient client, Guid tenantId, Guid chatbotId, string flowJson)
    {
        var ct = TestContext.Current.CancellationToken;
        var url = $"{AutomationsUrl(tenantId)}/chatbots/{chatbotId}";
        var editor = (await client.GetFromJsonAsync<ChatbotEditorDto>(url, ct))!;
        using var draft = new HttpRequestMessage(HttpMethod.Put, $"{url}/draft")
        {
            Content = JsonContent.Create(new SaveDraftRequest(JsonDocument.Parse(flowJson).RootElement, JsonDocument.Parse("{}").RootElement)),
        };
        draft.Headers.TryAddWithoutValidation("If-Match", $"\"{editor.Chatbot.Version}\"");
        var saved = await client.SendAsync(draft, ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using var publish = new HttpRequestMessage(HttpMethod.Post, $"{url}/publish");
        publish.Headers.TryAddWithoutValidation("If-Match", $"\"{(await saved.Content.ReadFromJsonAsync<DraftSavedDto>(ct))!.Version}\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(publish, ct)).StatusCode);
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
