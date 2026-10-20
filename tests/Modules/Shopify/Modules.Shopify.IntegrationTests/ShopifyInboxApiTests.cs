using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Quotations.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-15 §6.4 y §7: la bandeja de pedidos de punta a punta contra Postgres: la lista con sus filtros, el
/// resumen, el reintento por el camino real de la ingesta y del importador, y «Marcar como revisado».
/// </summary>
public sealed class ShopifyInboxApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrdersDefaultStateWaiting()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var waiting = await ShopifyInboxSeed.WaitingAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_201L);
        var ignored = await ShopifyInboxSeed.IgnoredAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_202L);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead);

        var page = await client.GetFromJsonAsync<JsonObject>(OrdersRoute(world.TenantId), Ct);
        var both = await client.GetFromJsonAsync<JsonObject>($"{OrdersRoute(world.TenantId)}?state=Waiting&state=Ignored", Ct);

        Assert.NotNull(page);
        Assert.Equal((1, 1, 50), (page["total"]!.GetValue<int>(), page["page"]!.GetValue<int>(), page["pageSize"]!.GetValue<int>()));
        var row = Assert.Single(page["items"]!.AsArray())!.AsObject();
        Assert.Equal(
            (waiting.ToString(), "Waiting", "product_unmatched", "Mi tienda", "#1001"),
            (Str(row, "id"), Str(row, "importState"), Str(row, "waitReason"), Str(row, "storeName"), Str(row, "orderName")));
        // La línea sin SKU llega como null dentro del arreglo (contrato con el frontend).
        Assert.Equal("""{"skus":[null]}""", row["waitDetail"]!.ToJsonString());
        Assert.NotNull(Str(row, "customerName"));
        Assert.NotNull(Str(row, "customerEmail"));
        Assert.Null(row["qepOrderNumber"]);
        Assert.Equal(
            new[] { ignored.ToString(), waiting.ToString() }.Order(StringComparer.Ordinal),
            both!["items"]!.AsArray().Select(item => Str(item!.AsObject(), "id")).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OrdersFilterByConnectionAndRejectUnknownState()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await ShopifyInboxSeed.WaitingAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_211L);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead);

        var other = await client.GetFromJsonAsync<JsonObject>($"{OrdersRoute(world.TenantId)}?connectionId={Guid.CreateVersion7()}", Ct);
        var unknown = await client.GetAsync($"{OrdersRoute(world.TenantId)}?state=Lost", Ct);
        var tooBig = await client.GetAsync($"{OrdersRoute(world.TenantId)}?pageSize=51", Ct);

        Assert.Equal(0, other!["total"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.NotNull((await unknown.Content.ReadFromJsonAsync<JsonObject>(Ct))!["errors"]!["state"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooBig.StatusCode);
        Assert.NotNull((await tooBig.Content.ReadFromJsonAsync<JsonObject>(Ct))!["errors"]!["pageSize"]);
    }

    [Fact]
    public async Task OrdersOtherTenantReturns403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var owner = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var row = await ShopifyInboxSeed.WaitingAsync(factory, owner.TenantId, owner.ConnectionId, 5_550_000_000_221L);
        var intruder = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, intruder.TenantId);
        using var client = CreateClient(
            factory, intruder.OwnerUserId, intruder.TenantId, OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);

        var list = await client.GetAsync(OrdersRoute(owner.TenantId), Ct);
        var summary = await client.GetAsync($"{OrdersRoute(owner.TenantId)}/summary", Ct);
        var retry = await client.PostAsync($"{OrdersRoute(owner.TenantId)}/{row}/retry", content: null, Ct);
        var review = await client.PostAsync($"{OrdersRoute(owner.TenantId)}/{row}/warnings/review", content: null, Ct);
        // Con su propio tenant en la ruta, la fila de otro no existe: 404, que no confirma nada del otro tenant.
        var own = await client.PostAsync($"{OrdersRoute(intruder.TenantId)}/{row}/retry", content: null, Ct);

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { list.StatusCode, summary.StatusCode, retry.StatusCode, review.StatusCode });
        Assert.Equal("authorization.denied", await CodeAsync(list));
        Assert.Equal(HttpStatusCode.NotFound, own.StatusCode);
        Assert.Equal(ShopifyErrorCodes.OrderNotFound, await CodeAsync(own));
    }

    // Sin la política el síntoma sería 500 (CLAUDE.md); y con el módulo apagado, 403 tenancy.module_not_enabled.
    [Fact]
    public async Task OrdersWithoutPermissionOrModuleReturn403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var row = await ShopifyInboxSeed.WaitingAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_231L);
        using var reader = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead);
        using var nobody = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, ShopifyPermissions.ConnectionRead);

        var list = await nobody.GetAsync(OrdersRoute(world.TenantId), Ct);
        var summary = await nobody.GetAsync($"{OrdersRoute(world.TenantId)}/summary", Ct);
        var retry = await reader.PostAsync($"{OrdersRoute(world.TenantId)}/{row}/retry", content: null, Ct);
        var review = await reader.PostAsync($"{OrdersRoute(world.TenantId)}/{row}/warnings/review", content: null, Ct);
        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { list.StatusCode, summary.StatusCode, retry.StatusCode, review.StatusCode });

        await ExecuteAsync(
            connectionString,
            "UPDATE tenancy.tenant_modules SET status = 'inactive', status_changed_at = now() WHERE tenant_id = @t AND module_key = 'shopify'",
            ("t", world.TenantId));
        var off = await reader.GetAsync(OrdersRoute(world.TenantId), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", await CodeAsync(off));
    }

    [Fact]
    public async Task SummaryEmptyTenantAllZeros()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, tenant.TenantId);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, OrdersPermissions.OrderRead);

        var response = await client.GetAsync($"{OrdersRoute(tenant.TenantId)}/summary", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal(
            """{"cancelledAfterInvoice":0,"importedToday":0,"waiting":0,"withWarnings":0}""",
            new JsonObject(body!.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))).ToJsonString());
    }

    [Fact]
    public async Task SummaryCountsEachBucket()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await ShopifyInboxSeed.WaitingAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_241L);
        await ShopifyInboxSeed.ImportedAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_242L, """[{"code":"total_difference","detail":null}]""");
        await ShopifyInboxSeed.ImportedAsync(
            factory, world.TenantId, world.ConnectionId, 5_550_000_000_243L, null, DateTimeOffset.UtcNow.AddDays(-2));
        await ShopifyInboxSeed.CancelledAfterInvoiceAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_244L);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead);

        var body = await client.GetFromJsonAsync<JsonObject>($"{OrdersRoute(world.TenantId)}/summary", Ct);

        Assert.Equal(
            (1, 1, 1, 1),
            (body!["waiting"]!.GetValue<int>(), body["withWarnings"]!.GetValue<int>(), body["cancelledAfterInvoice"]!.GetValue<int>(),
                body["importedToday"]!.GetValue<int>()));
    }

    // §6.4: la fila espera por un SKU que no existe; se crea el producto y el reintento la importa en el request, por la ingesta y
    // el importador reales, y devuelve la fila con el número del pedido de QEP.
    [Fact]
    public async Task RetryResolvedProductImports()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await world.ConfigureStoreAsync(factory);
        var snapshot = await ShopifyInboxSeed.SnapshotAsync(factory, 5_550_000_000_251L);
        snapshot = snapshot with { Lines = [.. snapshot.Lines.Select(line => line with { Sku = "NEW-001" })] };
        Guid rowId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var connection = await scope.ServiceProvider.GetRequiredService<IShopifyConnectionDirectory>().FindByShopDomainAsync(Shop, Ct);
            var row = await scope.ServiceProvider.GetRequiredService<ShopifyOrderIngestion>().ApplyAsync(connection!, snapshot, Ct);
            Assert.Equal((ShopifyImportState.Waiting, ShopifyWaitReasons.ProductUnmatched), (row.ImportState, row.WaitReason));
            rowId = row.Id;
        }

        await CreateProductAsync(world.Tenant.Seeder, world.TenantId, "NEW-001", "Producto nuevo", 50_000m, world.Tax19);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderManage);

        var response = await client.PostAsync($"{OrdersRoute(world.TenantId)}/{rowId}/retry", content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal(("Imported", 2), (Str(body, "importState"), body["attempts"]!.GetValue<int>()));
        Assert.Null(body["waitReason"]);
        var orderNumber = await ScalarAsync<string>(
            connectionString, "SELECT o.order_number FROM quotations.orders o JOIN shopify.orders m ON m.qep_order_id = o.id WHERE m.id = @id", ("id", rowId));
        Assert.Equal(orderNumber, Str(body, "qepOrderNumber"));
        Assert.StartsWith("PED-", orderNumber, StringComparison.Ordinal);

        // Ya importada: otro reintento es 422 not_waiting.
        var again = await client.PostAsync($"{OrdersRoute(world.TenantId)}/{rowId}/retry", content: null, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal(ShopifyErrorCodes.OrderNotWaiting, await CodeAsync(again));
    }

    [Fact]
    public async Task ReviewClearsFromWithWarnings()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var flagged = await ShopifyInboxSeed.ImportedAsync(
            factory, world.TenantId, world.ConnectionId, 5_550_000_000_261L, """[{"code":"shipping_address_unresolved","detail":null}]""");
        var clean = await ShopifyInboxSeed.ImportedAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_262L, null);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var withWarnings = $"{OrdersRoute(world.TenantId)}?state=Imported&withWarnings=true";

        var before = await client.GetFromJsonAsync<JsonObject>(withWarnings, Ct);
        var reviewed = await client.PostAsync($"{OrdersRoute(world.TenantId)}/{flagged}/warnings/review", content: null, Ct);
        var after = await client.GetFromJsonAsync<JsonObject>(withWarnings, Ct);
        var summary = await client.GetFromJsonAsync<JsonObject>($"{OrdersRoute(world.TenantId)}/summary", Ct);
        var noWarnings = await client.PostAsync($"{OrdersRoute(world.TenantId)}/{clean}/warnings/review", content: null, Ct);

        Assert.Equal(flagged.ToString(), Str(Assert.Single(before!["items"]!.AsArray())!.AsObject(), "id"));
        Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
        var row = (await reviewed.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.NotNull(row["warningsReviewedAt"]);
        Assert.Equal("""[{"code":"shipping_address_unresolved","detail":null}]""", row["warnings"]!.ToJsonString());
        Assert.Equal(0, after!["total"]!.GetValue<int>());
        Assert.Equal(0, summary!["withWarnings"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noWarnings.StatusCode);
        Assert.Equal(ShopifyErrorCodes.OrderNoWarnings, await CodeAsync(noWarnings));
    }

    // «hoy» es el día del tenant (America/Bogota), no el de UTC. Con el reloj fijo a las 07:00 del 19 en Bogotá, lo
    // importado a las 23:30 del 18 (04:30Z del 19) no es de hoy y lo de las 00:30 del 19 (05:30Z) sí. Cortar en UTC contaría los
    // dos (o ninguno, después de las 19:00 de Bogotá).
    [Fact]
    public async Task SummaryCutsTheDayInTheTenantTimeZone()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString)
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<BuildingBlocks.Application.IClock>();
                services.AddScoped<BuildingBlocks.Application.IClock>(_ => new FixedClock(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero)));
            },
        };
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await ShopifyInboxSeed.ImportedAsync(
            factory, world.TenantId, world.ConnectionId, 5_550_000_000_271L, null, new DateTimeOffset(2026, 10, 18, 23, 30, 0, TimeSpan.FromHours(-5)).ToUniversalTime());
        await ShopifyInboxSeed.ImportedAsync(
            factory, world.TenantId, world.ConnectionId, 5_550_000_000_272L, null, new DateTimeOffset(2026, 10, 19, 0, 30, 0, TimeSpan.FromHours(-5)).ToUniversalTime());
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead);

        var body = await client.GetFromJsonAsync<JsonObject>($"{OrdersRoute(world.TenantId)}/summary", Ct);

        Assert.Equal(1, body!["importedToday"]!.GetValue<int>());
    }

    // Con withWarnings=true y sin state la lista no filtra por estado, para que cierre con summary.withWarnings sin
    // que la pantalla conozca el enum; con state, se respeta.
    [Fact]
    public async Task OrdersWithWarningsWithoutStateIgnoresTheDefaultState()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var flagged = await ShopifyInboxSeed.ImportedAsync(
            factory, world.TenantId, world.ConnectionId, 5_550_000_000_281L, """[{"code":"total_difference","detail":null}]""");
        await ShopifyInboxSeed.WaitingAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_282L);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead);

        var all = await client.GetFromJsonAsync<JsonObject>($"{OrdersRoute(world.TenantId)}?withWarnings=true", Ct);
        var cancelledOnly = await client.GetFromJsonAsync<JsonObject>($"{OrdersRoute(world.TenantId)}?withWarnings=true&state=Cancelled", Ct);
        var summary = await client.GetFromJsonAsync<JsonObject>($"{OrdersRoute(world.TenantId)}/summary", Ct);

        Assert.Equal(flagged.ToString(), Str(Assert.Single(all!["items"]!.AsArray())!.AsObject(), "id"));
        Assert.Equal(summary!["withWarnings"]!.GetValue<int>(), all["total"]!.GetValue<int>());
        Assert.Equal(0, cancelledOnly!["total"]!.GetValue<int>());
    }

    // Decisión del owner del 2026-10-20: la fila trae lastErrorCodes siempre, [] sin error; lastError queda como texto de soporte.
    [Fact]
    public async Task OrdersCarryNormalizedLastErrorCodes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var rejected = await ShopifyInboxSeed.ImportedWithErrorAsync(
            factory, world.TenantId, world.ConnectionId, 5_550_000_000_291L, "order.order.not_pending; Ignored; order.order.not_pending");
        var clean = await ShopifyInboxSeed.ImportedAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_292L, null);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead);

        var page = await client.GetFromJsonAsync<JsonObject>($"{OrdersRoute(world.TenantId)}?state=Imported", Ct);

        var rows = page!["items"]!.AsArray().Select(item => item!.AsObject()).ToDictionary(row => Str(row, "id")!);
        Assert.Equal("""["order.order.not_pending","shopify.order.sync_failed"]""", rows[rejected.ToString()]["lastErrorCodes"]!.ToJsonString());
        Assert.Equal("order.order.not_pending; Ignored; order.order.not_pending", Str(rows[rejected.ToString()], "lastError"));
        Assert.Equal("[]", rows[clean.ToString()]["lastErrorCodes"]!.ToJsonString());
    }

    // Sin tope, (page - 1) * pageSize da la vuelta a un OFFSET negativo y Postgres responde 500.
    [Fact]
    public async Task OrdersPageBeyondTheCapReturns422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, tenant.TenantId);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, OrdersPermissions.OrderRead);

        var response = await client.GetAsync($"{OrdersRoute(tenant.TenantId)}?page={(int.MaxValue / 50) + 1}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.NotNull((await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["errors"]!["page"]);
    }

    private static string OrdersRoute(Guid tenantId) => $"/api/v1/tenants/{tenantId}/shopify/orders";

    private static string? Str(JsonObject body, string key) => body[key]?.GetValue<string>();

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))?["code"]?.GetValue<string>();
}

internal sealed class FixedClock(DateTimeOffset now) : BuildingBlocks.Application.IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}
