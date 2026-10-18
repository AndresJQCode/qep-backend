using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Integrations.Application;
using Modules.Quotations.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using Modules.Shopify.Infrastructure.Admin;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-15 §5.3 y §7: «Sincronizar ahora» (<c>POST …/shopify/stores/{connectionId}/sync</c>) de punta a punta contra
/// Postgres, con el host entero (directorio de conexiones, importador y reacción reales) y Shopify como un
/// <see cref="HttpMessageHandler"/> falso detrás del cliente GraphQL real. Al final, las piezas de persistencia de la conciliación
/// contra la base: el orden de los <c>Waiting</c> y el lease.
/// </summary>
public sealed class ShopifySyncApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SyncImportsAndReturnsCounts()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin().WithOrder(Shop, GraphQlFixture("taxes-included"));
        using var factory = shopify.Factory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await world.ConfigureStoreAsync(factory);
        var before = await LastReconciledAsync(connectionString, world.ConnectionId);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderManage);

        var response = await client.PostAsync(SyncRoute(world.TenantId, world.ConnectionId), content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.NotNull(body);
        Assert.Equal(
            ["cancelled", "durationMs", "fetched", "ignored", "imported", "waiting"],
            body.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal((1, 1, 0, 0, 0), (Int(body, "fetched"), Int(body, "imported"), Int(body, "waiting"), Int(body, "cancelled"), Int(body, "ignored")));
        Assert.Equal(
            "Imported|954889:5550000000001",
            await ScalarAsync<string>(
                connectionString,
                """
                SELECT m.import_state || '|' || o.external_key
                FROM shopify.orders m JOIN quotations.orders o ON o.id = m.qep_order_id AND o.tenant_id = m.tenant_id
                WHERE m.connection_id = @c
                """,
                ("c", world.ConnectionId)));
        // La ventana avanzó y el lease quedó libre.
        Assert.True(await LastReconciledAsync(connectionString, world.ConnectionId) > before);
        Assert.Equal(0L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.stores WHERE connection_id = @c AND reconcile_lease_until IS NOT NULL", ("c", world.ConnectionId)));
        // Con el token de la tienda, contra su dominio y la versión de la configuración.
        Assert.All(shopify.Requests, request =>
        {
            Assert.Equal($"https://{Shop}/admin/api/2026-10/graphql.json", request.Uri.ToString());
            Assert.Equal("shpat_sentinel_token", request.Token);
        });
        // La ventana: desde el last_reconciled_at de la tienda configurada, menos 5 minutos.
        using var list = JsonDocument.Parse(shopify.Requests[0].Body);
        Assert.Equal(
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"updated_at:>='{before.AddMinutes(-5):yyyy-MM-ddTHH:mm:ss}Z'"),
            list.RootElement.GetProperty("variables").GetProperty("query").GetString());
    }

    [Fact]
    public async Task SyncWhileRunningReturns409()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin();
        using var factory = shopify.Factory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await world.ConfigureStoreAsync(factory);
        await ExecuteAsync(
            connectionString, "UPDATE shopify.stores SET reconcile_lease_until = now() + interval '10 minutes' WHERE connection_id = @c",
            ("c", world.ConnectionId));
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderManage);

        var response = await client.PostAsync(SyncRoute(world.TenantId, world.ConnectionId), content: null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ShopifyErrorCodes.StoreSyncInProgress, await CodeAsync(response));
        Assert.Empty(shopify.Requests);
        // El lease es de la otra corrida: sigue ahí.
        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.stores WHERE connection_id = @c AND reconcile_lease_until > now()", ("c", world.ConnectionId)));
    }

    // Sin la política, RequireAuthorization no resuelve y el síntoma sería 500 (CLAUDE.md); un 403 dice que la política existe.
    [Fact]
    public async Task SyncWithoutOrderManageReturns403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin();
        using var factory = shopify.Factory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, tenant.TenantId);
        var connectionId = await LinkStoreAsync(factory, tenant.TenantId, tenant.OwnerUserId);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, OrdersPermissions.OrderRead);

        var response = await client.PostAsync(SyncRoute(tenant.TenantId, connectionId), content: null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(shopify.Requests);
    }

    [Fact]
    public async Task SyncModuleOffReturns403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin();
        using var factory = shopify.Factory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var connectionId = await LinkStoreAsync(factory, tenant.TenantId, tenant.OwnerUserId);
        // D-S20: el signup lo concede; el operador lo apaga.
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source, status)
            VALUES (@tenantId, 'shopify', now(), 'manual', 'inactive')
            ON CONFLICT (tenant_id, module_key) DO UPDATE SET status = 'inactive', status_changed_at = now()
            """,
            ("tenantId", tenant.TenantId));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, OrdersPermissions.OrderManage);

        var response = await client.PostAsync(SyncRoute(tenant.TenantId, connectionId), content: null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", await CodeAsync(response));
        Assert.Empty(shopify.Requests);
    }

    // La conexión es de otro tenant: 403, nunca 404 (un 404 confirmaría que el id existe en otro tenant), y Shopify ni se llama.
    [Fact]
    public async Task SyncOtherTenantConnectionReturns403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin().WithOrder(Shop, GraphQlFixture("taxes-included"));
        using var factory = shopify.Factory(connectionString);
        var owner = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, owner.TenantId);
        var foreignConnection = await LinkStoreAsync(factory, owner.TenantId, owner.OwnerUserId);
        var intruder = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, intruder.TenantId);
        using var client = CreateClient(factory, intruder.OwnerUserId, intruder.TenantId, OrdersPermissions.OrderManage);

        var foreign = await client.PostAsync(SyncRoute(intruder.TenantId, foreignConnection), content: null, Ct);
        var unknown = await client.PostAsync(SyncRoute(intruder.TenantId, Guid.CreateVersion7()), content: null, Ct);
        var otherRoute = await client.PostAsync(SyncRoute(owner.TenantId, foreignConnection), content: null, Ct);

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { foreign.StatusCode, unknown.StatusCode, otherRoute.StatusCode });
        Assert.Equal("authorization.denied", await CodeAsync(foreign));
        Assert.Equal("authorization.denied", await CodeAsync(unknown));
        Assert.Empty(shopify.Requests);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.orders"));
    }

    [Fact]
    public async Task SyncPausedConnectionReturns422NotOperational()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin().WithOrder(Shop, GraphQlFixture("taxes-included"));
        using var factory = shopify.Factory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, tenant.TenantId);
        var connectionId = await LinkStoreAsync(factory, tenant.TenantId, tenant.OwnerUserId);
        await PauseAsync(factory, tenant.TenantId, connectionId);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, OrdersPermissions.OrderManage);

        var response = await client.PostAsync(SyncRoute(tenant.TenantId, connectionId), content: null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ShopifyErrorCodes.StoreNotOperational, await CodeAsync(response));
        Assert.Empty(shopify.Requests);
    }

    // §5.3: 401 de Shopify → la conexión queda NeedsAttention con credentials_rejected y sale del ciclo; la ventana no se mueve.
    [Fact]
    public async Task SyncRejectedByShopifyReportsTheConnection()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin { Status = HttpStatusCode.Unauthorized };
        using var factory = shopify.Factory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await world.ConfigureStoreAsync(factory);
        var before = await LastReconciledAsync(connectionString, world.ConnectionId);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderManage);

        var response = await client.PostAsync(SyncRoute(world.TenantId, world.ConnectionId), content: null, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ShopifyErrorCodes.StoreNotOperational, await CodeAsync(response));
        Assert.Equal(
            "NeedsAttention",
            await ScalarAsync<string>(connectionString, "SELECT status FROM integrations.connections WHERE id = @c", ("c", world.ConnectionId)));
        Assert.Equal(before, await LastReconciledAsync(connectionString, world.ConnectionId));
        var problem = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("shpat_sentinel_token", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListWaitingReturnsTheOldestOrdersFirstUpToTheLimit()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new FakeShopifyAdmin().Factory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var mirrors = scope.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>();
            foreach (var (id, ageDays, imported) in new[] { (1L, 1, false), (2L, 5, false), (3L, 9, true), (4L, 3, false) })
            {
                var created = now.AddDays(-ageDays);
                var row = ShopifyOrderMirror.Create(
                    tenantId, connectionId, id, $$"""{"orderId":{{id}}}""", created, new ShopifyOrderHeader($"#{id}", null, created, null, "paid", "COP", 1m, true));
                if (imported)
                {
                    row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, now);
                }

                mirrors.Add(row);
            }

            // Otra tienda del mismo tenant y la misma tienda en otro tenant no entran.
            mirrors.Add(ShopifyOrderMirror.Create(
                tenantId, Guid.CreateVersion7(), 9L, "{}", now.AddDays(-30), new ShopifyOrderHeader("#9", null, now.AddDays(-30), null, null, "COP", 1m, true)));
            mirrors.Add(ShopifyOrderMirror.Create(
                Guid.CreateVersion7(), connectionId, 8L, "{}", now.AddDays(-30), new ShopifyOrderHeader("#8", null, now.AddDays(-30), null, null, "COP", 1m, true)));
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var reading = factory.Services.CreateAsyncScope();
        var waiting = await reading.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>().ListWaitingAsync(tenantId, connectionId, 2, Ct);

        Assert.Equal([2L, 4L], waiting.Select(order => order.ShopifyOrderId));
        Assert.Equal("""{"orderId": 2}""", waiting[0].Payload);
    }

    [Fact]
    public async Task LeaseIsExclusiveUntilReleasedOrExpired()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new FakeShopifyAdmin().Factory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IShopifyStoreRepository>().Add(ShopifyStore.Create(connectionId, tenantId, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var first = factory.Services.CreateAsyncScope();
        await using var second = factory.Services.CreateAsyncScope();
        var leases = first.ServiceProvider.GetRequiredService<IShopifyReconcileLeases>();
        var other = second.ServiceProvider.GetRequiredService<IShopifyReconcileLeases>();
        var reconciledAt = new DateTimeOffset(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

        var mine = await leases.TryAcquireAsync(tenantId, connectionId, TimeSpan.FromMinutes(30), Ct);
        Assert.NotNull(mine);
        Assert.Null(await other.TryAcquireAsync(tenantId, connectionId, TimeSpan.FromMinutes(30), Ct));
        // Otro tenant con el mismo id de conexión no toca la fila.
        Assert.Null(await other.TryAcquireAsync(Guid.CreateVersion7(), connectionId, TimeSpan.FromMinutes(30), Ct));
        await leases.RecordReconciledAsync(tenantId, connectionId, reconciledAt, Ct);
        await leases.ReleaseAsync(tenantId, connectionId, mine.Value, Ct);
        Assert.NotNull(await other.TryAcquireAsync(tenantId, connectionId, TimeSpan.FromMinutes(30), Ct));
        await ExecuteAsync(connectionString, "UPDATE shopify.stores SET reconcile_lease_until = now() - interval '1 second'");
        Assert.NotNull(await leases.TryAcquireAsync(tenantId, connectionId, TimeSpan.FromMinutes(30), Ct));
        // Ni el lease ni la ventana suben la versión de la tienda: es el If-Match del PUT.
        Assert.Equal(
            $"{reconciledAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}|1",
            await ScalarAsync<string>(
                connectionString,
                "SELECT to_char(last_reconciled_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') || '|' || version FROM shopify.stores"));
        // Una fila que no existe no se puede tomar: la conciliación la crea antes.
        Assert.Null(await leases.TryAcquireAsync(tenantId, Guid.CreateVersion7(), TimeSpan.FromMinutes(30), Ct));
    }

    // Minor 5: una corrida que tardó más que su lease no suelta el de la corrida que lo tomó después.
    [Fact]
    public async Task ReleaseDoesNotFreeTheLeaseOfAnotherRun()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new FakeShopifyAdmin().Factory(connectionString);
        var tenantId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IShopifyStoreRepository>().Add(ShopifyStore.Create(connectionId, tenantId, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(Ct);
        }

        await using var scopeA = factory.Services.CreateAsyncScope();
        await using var scopeB = factory.Services.CreateAsyncScope();
        var slow = scopeA.ServiceProvider.GetRequiredService<IShopifyReconcileLeases>();
        var next = scopeB.ServiceProvider.GetRequiredService<IShopifyReconcileLeases>();
        var slowLease = await slow.TryAcquireAsync(tenantId, connectionId, TimeSpan.FromMinutes(30), Ct);
        Assert.NotNull(slowLease);
        await ExecuteAsync(connectionString, "UPDATE shopify.stores SET reconcile_lease_until = now() - interval '1 second'");
        Assert.NotNull(await next.TryAcquireAsync(tenantId, connectionId, TimeSpan.FromMinutes(30), Ct));

        await slow.ReleaseAsync(tenantId, connectionId, slowLease.Value, Ct);

        Assert.Equal(1L, await ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM shopify.stores WHERE connection_id = @c AND reconcile_lease_until > now()", ("c", connectionId)));
    }

    // D-S4 (ruling S25): la tienda que nunca recibió nada nace, en su primera sincronización, con la ventana en el enlace (la
    // creación de la conexión) y no en «ahora».
    [Fact]
    public async Task SyncFirstContactStartsTheWindowAtTheLink()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var shopify = new FakeShopifyAdmin();
        using var factory = shopify.Factory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, tenant.TenantId);
        var connectionId = await LinkStoreAsync(factory, tenant.TenantId, tenant.OwnerUserId);
        await ExecuteAsync(
            connectionString,
            "UPDATE integrations.connections SET created_at = timestamptz '2026-10-18 10:00:00+00' WHERE id = @c",
            ("c", connectionId));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, OrdersPermissions.OrderManage);

        var response = await client.PostAsync(SyncRoute(tenant.TenantId, connectionId), content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var list = JsonDocument.Parse(shopify.Requests[0].Body);
        Assert.Equal("updated_at:>='2026-10-18T09:55:00Z'", list.RootElement.GetProperty("variables").GetProperty("query").GetString());
    }

    private static string SyncRoute(Guid tenantId, Guid connectionId) =>
        $"/api/v1/tenants/{tenantId}/shopify/stores/{connectionId}/sync";

    private static int Int(JsonObject body, string name) => body[name]!.GetValue<int>();

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        return problem?["code"]?.GetValue<string>();
    }

    private static Task<DateTime> LastReconciledAsync(string connectionString, Guid connectionId) =>
        ScalarAsync<DateTime>(connectionString, "SELECT last_reconciled_at FROM shopify.stores WHERE connection_id = @c", ("c", connectionId));

    private static async Task PauseAsync(QepApiFactory factory, Guid tenantId, Guid connectionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var connection = await scope.ServiceProvider.GetRequiredService<IIntegrationConnectionRepository>().FindAsync(tenantId, connectionId, Ct);
        Assert.NotNull(connection);
        connection.Pause(DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<IIntegrationsUnitOfWork>().SaveChangesAsync(Ct);
    }
}

/// <summary>
/// Shopify detrás del cliente GraphQL real: por dominio, los pedidos que devuelve <c>orders(first: 50)</c> (sólo ids, una página)
/// y el detalle de cada uno con <c>order(id:)</c>. Anota cada request con su token. <see cref="Status"/> distinto de 200 lo
/// responde a todo.
/// </summary>
internal sealed class FakeShopifyAdmin : HttpMessageHandler
{
    private readonly Dictionary<string, List<JsonObject>> orders = new(StringComparer.Ordinal);

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public List<(Uri Uri, string? Token, string Body)> Requests { get; } = [];

    public FakeShopifyAdmin WithOrder(string shop, JsonObject order)
    {
        lock (orders)
        {
            if (!orders.TryGetValue(shop, out var list))
            {
                orders[shop] = list = [];
            }

            list.Add(order);
        }

        return this;
    }

    /// <summary>El host con este Shopify detrás del cliente <c>shopify.admin</c>.</summary>
    public QepApiFactory Factory(string connectionString, Action<IServiceCollection>? configure = null) => new(connectionString)
    {
        ConfigureTestServices = services =>
        {
            services.AddHttpClient(ShopifyGraphQlClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => this);
            configure?.Invoke(services);
        },
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var token = request.Headers.TryGetValues("X-Shopify-Access-Token", out var values) ? values.Single() : null;
        lock (Requests)
        {
            Requests.Add((request.RequestUri!, token, body));
        }

        if (Status != HttpStatusCode.OK)
        {
            return Json(Status, """{"errors":"[API] Invalid API key or access token (unrecognized login or wrong password)"}""");
        }

        List<JsonObject> shop;
        lock (orders)
        {
            shop = orders.TryGetValue(request.RequestUri!.Host, out var list) ? [.. list] : [];
        }

        using var document = JsonDocument.Parse(body);
        var variables = document.RootElement.GetProperty("variables");
        if (body.Contains("orders(", StringComparison.Ordinal))
        {
            var nodes = new JsonArray([.. shop.Select(order => (JsonNode)new JsonObject { ["id"] = order["id"]!.DeepClone() })]);
            return Json(HttpStatusCode.OK, new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["orders"] = new JsonObject
                    {
                        ["pageInfo"] = new JsonObject { ["hasNextPage"] = false, ["endCursor"] = null },
                        ["nodes"] = nodes,
                    },
                },
            }.ToJsonString());
        }

        var id = variables.GetProperty("id").GetString();
        var match = shop.FirstOrDefault(order => order["id"]!.GetValue<string>() == id);
        return Json(HttpStatusCode.OK, new JsonObject { ["data"] = new JsonObject { ["order"] = match?.DeepClone() } }.ToJsonString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
