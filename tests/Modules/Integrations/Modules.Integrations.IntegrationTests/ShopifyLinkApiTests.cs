using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Integrations.Api;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;
using static Modules.Integrations.IntegrationTests.IntegrationsApiHarness;

namespace Modules.Integrations.IntegrationTests;

/// <summary>La instalación de verdad por HTTP, hasta antes del enlace: inicio A, canje contra <see cref="FakeShopifyHandler"/>
/// y el <c>installId</c>. La comparten las pruebas del enlace, la de fugas y la del re-cifrado.</summary>
internal static class ShopifyFlow
{
    public const string Shop = "mi-tienda.myshopify.com";

    public const string SentinelAccessToken = "shpat_link-token-SENTINEL-6d5c";

    public const string SentinelCode = "shopify-code-SENTINEL-link";

    private const string Timestamp = "1760796000";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string LinkUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/integrations/shopify/link";

    /// <summary>Los webhooks (sin ninguno previo) y el canje sano: el orden importa, ver
    /// <see cref="FakeShopifyHandler.ScriptWebhooks"/>.</summary>
    public static void ScriptHappyShop(FakeShopifyHandler shopify, string token = SentinelAccessToken, bool rejectWebhooks = false)
    {
        shopify.ScriptWebhooks(Shop, ShopifyWebhookCallbackUrl, rejectWebhooks);
        shopify.ScriptHappyInstall(Shop, token);
    }

    /// <summary>Inicio A con <paramref name="client"/> en <paramref name="tenantId"/> y canje anónimo; devuelve el installId.</summary>
    public static async Task<Guid> InstallAsync(WebApplicationFactory<Program> factory, HttpClient client, Guid tenantId)
    {
        var start = await SendAsync(
            client, HttpMethod.Post, $"/api/v1/tenants/{tenantId}/integrations/shopify/install/start",
            new { shop = Shop, redirectUri = ShopifyRedirectUri });
        start.EnsureSuccessStatusCode();
        var authorize = await start.Content.ReadFromJsonAsync<ShopifyAuthorizeResponse>(Ct);
        var state = Uri.UnescapeDataString(new Uri(authorize!.AuthorizeUrl).Query.TrimStart('?').Split('&')
            .Single(pair => pair.StartsWith("state=", StringComparison.Ordinal))[6..]);

        using var anonymous = factory.CreateClient();
        var hmac = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(SentinelShopifyClientSecret),
            Encoding.UTF8.GetBytes($"code={SentinelCode}&shop={Shop}&state={state}&timestamp={Timestamp}")));
        var complete = await SendAsync(
            anonymous, HttpMethod.Post, IntegrationsEndpoints.ShopifyInstallCompleteRoute,
            new { code = SentinelCode, shop = Shop, state, hmac, timestamp = Timestamp, redirectUri = ShopifyRedirectUri });
        complete.EnsureSuccessStatusCode();
        return (await complete.Content.ReadFromJsonAsync<ShopifyInstallCompletedResponse>(Ct))!.InstallId;
    }
}

/// <summary>
/// Spec 2026-10-15 §4.3 paso 6 y §7 por HTTP: el enlace crea la conexión, la ruta y los webhooks contra
/// <see cref="FakeShopifyHandler"/>, borra la instalación y audita; revalida permiso, módulo y tenant (403); la misma
/// tienda en otro tenant es 422 <c>shop_already_linked</c> con el nombre de ese tenant (Review Focus 3).
/// </summary>
public sealed class ShopifyLinkApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<long> CountInstallsAsync(string connectionString) =>
        ScalarAsync<long>(connectionString, "SELECT count(*) FROM integrations.shopify_installs");

    [Fact]
    public async Task LinkCreatesConnectionRouteAndWebhooks()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler);
        var tenant = await RegisterTenantAsync(factory);
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var installId = await ShopifyFlow.InstallAsync(factory, owner, tenant.TenantId);

        var response = await SendAsync(owner, HttpMethod.Post, ShopifyFlow.LinkUrl(tenant.TenantId), new { installId });
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain("SENTINEL", text, StringComparison.Ordinal);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.NotNull(connection);
        Assert.Equal("shopify", connection.ProviderKey);
        Assert.Equal("Mi Tienda", connection.Name);
        Assert.Equal("Active", connection.Status);
        Assert.Equal($"/api/v1/tenants/{tenant.TenantId}/integrations/connections/{connection.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(ShopifyFlow.Shop, connection.Fields["shopDomain"]);
        Assert.True(connection.Secrets["accessToken"].Readable);

        // La conexión, con los cinco ids; la ruta del webhook; la instalación ya no existe.
        Assert.Equal(
            "gid://shopify/WebhookSubscription/ORDERS_CREATE,gid://shopify/WebhookSubscription/ORDERS_UPDATED,"
            + "gid://shopify/WebhookSubscription/ORDERS_PAID,gid://shopify/WebhookSubscription/ORDERS_CANCELLED,"
            + "gid://shopify/WebhookSubscription/APP_UNINSTALLED",
            await ScalarAsync<string>(
                connectionString,
                "SELECT fields->>'webhookSubscriptionIds' FROM integrations.connections WHERE id = @id AND tenant_id = @tenantId AND status = 'Active'",
                ("id", connection.Id),
                ("tenantId", tenant.TenantId)));
        Assert.Equal(1, await ScalarAsync<long>(
            connectionString,
            """
            SELECT count(*) FROM integrations.connection_routes
            WHERE provider_key = 'shopify' AND external_id = @shop AND account_id IS NULL AND tenant_id = @tenantId AND connection_id = @id
            """,
            ("shop", ShopifyFlow.Shop),
            ("tenantId", tenant.TenantId),
            ("id", connection.Id)));
        Assert.Equal(0, await CountInstallsAsync(connectionString));
        Assert.Equal(1, await ScalarAsync<long>(
            connectionString,
            """
            SELECT count(*) FROM audit.entries
            WHERE source = 'integrations' AND action = 'integrations.connection.created' AND resource_id = @id
              AND changed_fields::text LIKE '%onboarding:shopify_oauth:A%'
            """,
            ("id", connection.Id.ToString())));

        // Las cinco altas, con el token canjeado, el enum de GraphQL, formato JSON y la URL configurada.
        var creates = factory.ShopifyHandler.Requests
            .Where(request => request.Body?.Contains("webhookSubscriptionCreate", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(5, creates.Length);
        Assert.All(creates, request =>
        {
            Assert.Equal(ShopifyFlow.SentinelAccessToken, request.AccessToken);
            Assert.Contains($"\"uri\":\"{ShopifyWebhookCallbackUrl}\"", request.Body, StringComparison.Ordinal);
            Assert.Contains("\"format\":\"JSON\"", request.Body, StringComparison.Ordinal);
        });
        Assert.Contains(creates, request => request.Body!.Contains("\"topic\":\"APP_UNINSTALLED\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LinkOtherTenantSameShopReturns422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler);
        var first = await RegisterTenantAsync(factory);
        var second = await RegisterTenantAsync(factory);
        await ExecuteAsync(
            connectionString, "UPDATE tenancy.tenants SET display_name = 'Distribuidora Andina' WHERE id = @id", ("id", first.TenantId));
        using var firstOwner = CreateClient(factory, first.OwnerUserId, first.TenantId, ManagePermissions);
        using var secondOwner = CreateClient(factory, second.OwnerUserId, second.TenantId, ManagePermissions);
        var firstInstall = await ShopifyFlow.InstallAsync(factory, firstOwner, first.TenantId);
        var linked = await SendAsync(firstOwner, HttpMethod.Post, ShopifyFlow.LinkUrl(first.TenantId), new { installId = firstInstall });
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        var secondInstall = await ShopifyFlow.InstallAsync(factory, secondOwner, second.TenantId);

        var response = await SendAsync(secondOwner, HttpMethod.Post, ShopifyFlow.LinkUrl(second.TenantId), new { installId = secondInstall });
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("integrations.shopify.shop_already_linked", (await ProblemAsync(response)).Code);
        Assert.Contains("Distribuidora Andina", text, StringComparison.Ordinal);
        Assert.DoesNotContain(first.TenantId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await CountConnectionsAsync(connectionString, second.TenantId));
        // La instalación del segundo sigue ahí: el error no la consume.
        Assert.Equal(1, await CountInstallsAsync(connectionString));
    }

    // Ruling S6: GET /installs/{id} lo lee cualquiera con el id, así que el enlace revalida permiso, tenant, módulo y origen.
    [Fact]
    public async Task LinkRevalidatesPermissionTenantModuleAndOrigin()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        using var reader = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        using var otherOwner = CreateClient(factory, other.OwnerUserId, other.TenantId, ManagePermissions);
        var installId = await ShopifyFlow.InstallAsync(factory, owner, tenant.TenantId);
        var body = new { installId };

        var withoutPermission = await SendAsync(reader, HttpMethod.Post, ShopifyFlow.LinkUrl(tenant.TenantId), body);
        var crossTenantRoute = await SendAsync(owner, HttpMethod.Post, ShopifyFlow.LinkUrl(other.TenantId), body);
        // Iniciada desde el primer tenant: el dueño del otro tiene permiso en el suyo, pero la instalación no es suya.
        var mismatch = await SendAsync(otherOwner, HttpMethod.Post, ShopifyFlow.LinkUrl(other.TenantId), body);
        await SetModuleStatusAsync(connectionString, tenant.TenantId, "shopify", "inactive");
        var moduleOff = await SendAsync(owner, HttpMethod.Post, ShopifyFlow.LinkUrl(tenant.TenantId), body);
        var unknown = await SendAsync(otherOwner, HttpMethod.Post, ShopifyFlow.LinkUrl(other.TenantId), new { installId = Guid.NewGuid() });
        var missing = await SendAsync(otherOwner, HttpMethod.Post, ShopifyFlow.LinkUrl(other.TenantId), new { });

        Assert.Equal(HttpStatusCode.Forbidden, withoutPermission.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossTenantRoute.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, mismatch.StatusCode);
        Assert.Equal("integrations.shopify.install_tenant_mismatch", (await ProblemAsync(mismatch)).Code);
        Assert.Equal(HttpStatusCode.Forbidden, moduleOff.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(moduleOff)).Code);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        var (missingCode, missingKeys) = await ProblemAsync(missing);
        Assert.Equal("validation.failed", missingCode);
        Assert.Equal(["installId"], missingKeys);
        Assert.Equal(1, await CountInstallsAsync(connectionString));
        Assert.Equal(0, await CountConnectionsAsync(connectionString, tenant.TenantId));
        Assert.Equal(0, await CountConnectionsAsync(connectionString, other.TenantId));
    }

    [Fact]
    public async Task LinkWebhooksRejectedCreatesTheConnectionNeedingAttention()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler, rejectWebhooks: true);
        var tenant = await RegisterTenantAsync(factory);
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var installId = await ShopifyFlow.InstallAsync(factory, owner, tenant.TenantId);

        var response = await SendAsync(owner, HttpMethod.Post, ShopifyFlow.LinkUrl(tenant.TenantId), new { installId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var connection = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.Equal("NeedsAttention", connection!.Status);
        Assert.Equal("webhooks_not_registered", connection.LastFailureCode);
        Assert.Equal(0, await CountInstallsAsync(connectionString));
    }

    // §4.1: desinstalar y volver a enlazar la misma tienda en el mismo tenant reutiliza la conexión, con el token nuevo.
    [Fact]
    public async Task LinkSameTenantTwiceReactivatesTheSameConnectionWithTheNewToken()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        ShopifyFlow.ScriptHappyShop(factory.ShopifyHandler);
        var tenant = await RegisterTenantAsync(factory);
        using var owner = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ManagePermissions);
        var first = await (await SendAsync(
                owner, HttpMethod.Post, ShopifyFlow.LinkUrl(tenant.TenantId),
                new { installId = await ShopifyFlow.InstallAsync(factory, owner, tenant.TenantId) }))
            .Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        await ExecuteAsync(
            connectionString,
            "UPDATE integrations.connections SET status = 'NeedsAttention', last_failure_code = 'app_uninstalled' WHERE id = @id",
            ("id", first!.Id));
        factory.ShopifyHandler.Reset();
        // Ya están los cinco: el reenlace no crea ninguno.
        factory.ShopifyHandler.ScriptWebhooks(
            ShopifyFlow.Shop, ShopifyWebhookCallbackUrl, false, "ORDERS_CREATE", "ORDERS_UPDATED", "ORDERS_PAID", "ORDERS_CANCELLED", "APP_UNINSTALLED");
        factory.ShopifyHandler.ScriptHappyInstall(ShopifyFlow.Shop, "shpat_second-token-SENTINEL");
        var secondInstall = await ShopifyFlow.InstallAsync(factory, owner, tenant.TenantId);
        var createsBefore = factory.ShopifyHandler.Requests.Count(request => request.Body?.Contains("webhookSubscriptionCreate", StringComparison.Ordinal) == true);

        var response = await SendAsync(owner, HttpMethod.Post, ShopifyFlow.LinkUrl(tenant.TenantId), new { installId = secondInstall, name = "Tienda principal" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var relinked = await response.Content.ReadFromJsonAsync<ConnectionResponse>(Ct);
        Assert.Equal(first.Id, relinked!.Id);
        Assert.Equal("Active", relinked.Status);
        Assert.Equal("Tienda principal", relinked.Name);
        Assert.Equal(1, await CountConnectionsAsync(connectionString, tenant.TenantId));
        Assert.Equal(createsBefore, factory.ShopifyHandler.Requests.Count(request => request.Body?.Contains("webhookSubscriptionCreate", StringComparison.Ordinal) == true));
        Assert.Equal(
            "shpat_second-token-SENTINEL",
            factory.ShopifyHandler.Requests.Last(request => request.Body?.Contains("webhookSubscriptions(first", StringComparison.Ordinal) == true).AccessToken);
        Assert.Equal(1, await ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM integrations.connections WHERE id = @id AND status = 'Active'",
            ("id", first.Id)));
    }
}
