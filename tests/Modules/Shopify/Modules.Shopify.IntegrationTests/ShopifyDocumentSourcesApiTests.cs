using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// <c>GET /stores/{connectionId}/document-sources</c>: las opciones del select «Atributo del documento» de la configuración de la
/// tienda, leídas del espejo de pedidos de esa tienda contra Postgres. El payload del espejo es el snapshot normalizado
/// (<see cref="ShopifyOrderIngestion.WritePayload"/>): los atributos llegan como objeto <c>attributes</c>, igual vengan de REST
/// (<c>note_attributes</c>) o de GraphQL (<c>customAttributes</c>).
/// </summary>
public sealed class ShopifyDocumentSourcesApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DocumentSourcesOfAStoreWithoutOrdersOfferOnlyTheShippingCompany()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, connectionId) = await LinkedStoreAsync(factory, connectionString);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, IntegrationsPermissions.ConnectionRead);

        var response = await client.GetAsync(Route(tenant.TenantId, connectionId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal(["sources"], body.Select(pair => pair.Key));
        Assert.Equal(["shipping_address.company"], Sources(body));
    }

    // Los nombres de los pedidos de esta tienda, sin repetir y ordenados; los de otra tienda del tenant y los de otro tenant no
    // aparecen. El primer pedido es el fixture REST tal cual (documento, tipo_documento) pasado por el parser real.
    [Fact]
    public async Task DocumentSourcesListTheDistinctAttributeKeysOfThisStoreOnly()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, connectionId) = await LinkedStoreAsync(factory, connectionString);
        var (otherConnection, otherTenant) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await SeedAsync(factory, tenant.TenantId, connectionId, 5_550_000_000_301L, attributes: null);
        await SeedAsync(factory, tenant.TenantId, connectionId, 5_550_000_000_302L, Attributes(("Cédula", "1036000111"), ("documento", "x"), (" ", "y")));
        await SeedAsync(factory, tenant.TenantId, connectionId, 5_550_000_000_303L, Attributes());
        await SeedAsync(factory, tenant.TenantId, otherConnection, 5_550_000_000_304L, Attributes(("de_otra_tienda", "1")));
        await SeedAsync(factory, otherTenant, Guid.CreateVersion7(), 5_550_000_000_305L, Attributes(("de_otro_tenant", "1")));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, IntegrationsPermissions.ConnectionRead);

        var body = await client.GetFromJsonAsync<JsonObject>(Route(tenant.TenantId, connectionId), Ct);

        Assert.Equal(["shipping_address.company", "Cédula", "documento", "tipo_documento"], Sources(body!));
    }

    // El recorrido mira los pedidos más recientes de la tienda (shopify_created_at), no todo el espejo.
    [Fact]
    public async Task TheScanReadsOnlyTheMostRecentOrders()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenant, connectionId) = await LinkedStoreAsync(factory, connectionString);
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(factory, tenant.TenantId, connectionId, 5_550_000_000_311L, Attributes(("vieja", "1")), now.AddDays(-2));
        await SeedAsync(factory, tenant.TenantId, connectionId, 5_550_000_000_312L, Attributes(("nueva", "1")), now.AddDays(-1));
        await using var scope = factory.Services.CreateAsyncScope();
        var mirrors = scope.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>();

        var newest = await mirrors.ListAttributeKeysAsync(tenant.TenantId, connectionId, recentOrders: 1, Ct);
        var both = await mirrors.ListAttributeKeysAsync(tenant.TenantId, connectionId, recentOrders: 2, Ct);

        Assert.Equal(["nueva"], newest);
        Assert.Equal(["nueva", "vieja"], both.Order(StringComparer.Ordinal));
    }

    // La conexión es de otro tenant: 403, nunca 404, igual que GET /stores/{connectionId}. Sin el permiso de lectura, también 403.
    [Fact]
    public async Task DocumentSourcesOfAnotherTenantOrWithoutPermissionReturn403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (owner, connectionId) = await LinkedStoreAsync(factory, connectionString);
        await SeedAsync(factory, owner.TenantId, connectionId, 5_550_000_000_321L, Attributes(("secreto", "1")));
        var intruder = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, intruder.TenantId);
        using var foreignClient = CreateClient(factory, intruder.OwnerUserId, intruder.TenantId, IntegrationsPermissions.ConnectionRead);
        using var nobody = CreateClient(factory, owner.OwnerUserId, owner.TenantId, ShopifyPermissions.OrderRead);

        var foreign = await foreignClient.GetAsync(Route(intruder.TenantId, connectionId), Ct);
        var otherRoute = await foreignClient.GetAsync(Route(owner.TenantId, connectionId), Ct);
        var unknown = await foreignClient.GetAsync(Route(intruder.TenantId, Guid.CreateVersion7()), Ct);
        var withoutPermission = await nobody.GetAsync(Route(owner.TenantId, connectionId), Ct);

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { foreign.StatusCode, otherRoute.StatusCode, unknown.StatusCode, withoutPermission.StatusCode });
        Assert.Equal("authorization.denied", await CodeAsync(foreign));
        Assert.DoesNotContain("secreto", await foreign.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static async Task<(RegisteredTenant Tenant, Guid ConnectionId)> LinkedStoreAsync(QepApiFactory factory, string connectionString)
    {
        var tenant = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, tenant.TenantId);
        return (tenant, await LinkStoreAsync(factory, tenant.TenantId, tenant.OwnerUserId));
    }

    private static Dictionary<string, string> Attributes(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Una fila del espejo como la deja la ingesta: el fixture REST por el parser real y, si se dan, otros atributos.</summary>
    private static async Task SeedAsync(
        QepApiFactory factory,
        Guid tenantId,
        Guid connectionId,
        long orderId,
        IReadOnlyDictionary<string, string>? attributes,
        DateTimeOffset? createdAt = null)
    {
        var snapshot = await ShopifyInboxSeed.SnapshotAsync(factory, orderId);
        snapshot = snapshot with
        {
            Attributes = attributes ?? snapshot.Attributes,
            CreatedAt = createdAt ?? snapshot.CreatedAt,
        };
        await using var scope = factory.Services.CreateAsyncScope();
        var row = ShopifyOrderMirror.Create(
            tenantId, connectionId, orderId, ShopifyOrderIngestion.WritePayload(snapshot), snapshot.UpdatedAt, snapshot.ToHeader());
        row.MarkIgnored();
        scope.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>().Add(row);
        await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
    }

    private static string Route(Guid tenantId, Guid connectionId) =>
        $"/api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/document-sources";

    private static string[] Sources(JsonObject body) =>
        [.. body["sources"]!.AsArray().Select(item => item!.GetValue<string>())];

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))?["code"]?.GetValue<string>();
}
