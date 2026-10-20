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
/// Spec 2026-10-15 §6.1 y §7: la lista de tiendas y la configuración de cada una de punta a punta, con el host entero
/// (políticas, <c>If-Match</c>, el mapeo de errores de <c>ApiExceptionHandler</c> y los adaptadores reales de Integrations,
/// Tenancy, Companies y Customers) contra Postgres.
/// </summary>
public sealed class ShopifyStoresApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StoresListShowsWaitingCountAndIsConfigured()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await world.ConfigureStoreAsync(factory);
        await ShopifyInboxSeed.WaitingAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_101L);
        await ShopifyInboxSeed.WaitingAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_102L);
        await ShopifyInboxSeed.IgnoredAsync(factory, world.TenantId, world.ConnectionId, 5_550_000_000_103L);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, IntegrationsPermissions.ConnectionRead);

        var response = await client.GetAsync(StoresRoute(world.TenantId), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stores = await response.Content.ReadFromJsonAsync<JsonArray>(Ct);
        var store = Assert.Single(stores!)!.AsObject();
        Assert.Equal(
            ["connectionId", "isConfigured", "lastReconciledAt", "name", "shopDomain", "status", "waitingCount"],
            store.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal(
            (world.ConnectionId.ToString(), "Mi tienda", Shop, "Active", true, 2),
            (Str(store, "connectionId"), Str(store, "name"), Str(store, "shopDomain"), Str(store, "status"),
                store["isConfigured"]!.GetValue<bool>(), store["waitingCount"]!.GetValue<int>()));
        Assert.NotNull(store["lastReconciledAt"]);
    }

    // §6.1: una conexión que el módulo todavía no vio sale igual, sin configurar, con cero en espera (BFF: el número viaja) y sin
    // conciliación.
    [Fact]
    public async Task StoresListIncludesALinkedStoreTheModuleHasNotSeen()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, tenant.TenantId);
        var connectionId = await LinkStoreAsync(factory, tenant.TenantId, tenant.OwnerUserId);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, IntegrationsPermissions.ConnectionRead);

        var stores = await client.GetFromJsonAsync<JsonArray>(StoresRoute(tenant.TenantId), Ct);

        var store = Assert.Single(stores!)!.AsObject();
        Assert.Equal(
            (connectionId.ToString(), false, 0, true),
            (Str(store, "connectionId"), store["isConfigured"]!.GetValue<bool>(), store["waitingCount"]!.GetValue<int>(),
                store["lastReconciledAt"] is null));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.stores"));
    }

    [Fact]
    public async Task StoresPutThenGetRoundTrips()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = CreateClient(
            factory, world.Tenant.OwnerUserId, world.TenantId, IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage);

        // §6.1: la primera lectura crea la tienda vacía, con las claves por defecto y versión 1.
        var first = await client.GetAsync(StoreRoute(world.TenantId, world.ConnectionId), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("\"1\"", first.Headers.ETag?.Tag);
        var empty = (await first.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal((false, 1L, "Documento", "CC"), (empty["isConfigured"]!.GetValue<bool>(), empty["version"]!.GetValue<long>(), Str(empty, "documentAttributeKey"), Str(empty, "defaultIdentificationType")));

        // Sin If-Match, 428.
        var withoutVersion = await client.PutAsJsonAsync(StoreRoute(world.TenantId, world.ConnectionId), Body(world), Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, withoutVersion.StatusCode);

        var saved = await PutAsync(client, world.TenantId, world.ConnectionId, Body(world), "\"1\"");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("\"2\"", saved.Headers.ETag?.Tag);

        // La versión vieja, 412, y la tienda no cambia.
        var stale = await PutAsync(client, world.TenantId, world.ConnectionId, Body(world, documentAttributeKey: "Cédula"), "\"1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("concurrency.conflict", await CodeAsync(stale));

        var read = await client.GetFromJsonAsync<JsonObject>(StoreRoute(world.TenantId, world.ConnectionId), Ct);
        Assert.NotNull(read);
        Assert.Equal(
            (true, 2L, world.AdvisorMembershipId.ToString(), "Asesora Shopify", world.Company.Id.ToString(), "Origen Botánico SAS"),
            (read["isConfigured"]!.GetValue<bool>(), read["version"]!.GetValue<long>(), Str(read, "advisorMembershipId"),
                Str(read, "advisorName"), Str(read, "billingCompanyId"), Str(read, "billingCompanyName")));
        Assert.Equal(
            ("Bancolombia", world.Company.AccountNumber, "COP", world.ClassificationId.ToString(), "Tienda en línea"),
            (Str(read, "billingBankName"), Str(read, "billingAccountNumber"), Str(read, "billingCurrency"),
                Str(read, "defaultClassificationId"), Str(read, "classificationName")));
        Assert.Equal(("Documento", "Tipo de documento", "NIT"), (Str(read, "documentAttributeKey"), Str(read, "documentTypeAttributeKey"), Str(read, "defaultIdentificationType")));
        // La fila guardada: la versión es el token de concurrencia y quien guardó, una membresía.
        Assert.Equal(
            "2|NIT|true",
            await ScalarAsync<string>(
                connectionString,
                "SELECT version || '|' || default_identification_type || '|' || (updated_by IS NOT NULL) FROM shopify.stores WHERE connection_id = @c",
                ("c", world.ConnectionId)));
    }

    // El PUT deja shopify.store.updated en audit.entries en la misma transacción; el que pierde con 412 no deja nada.
    [Fact]
    public async Task StoresPutRecordsAuditAndAStalePutDoesNot()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, IntegrationsPermissions.ConnectionManage);

        var saved = await PutAsync(client, world.TenantId, world.ConnectionId, Body(world), "\"1\"");
        var stale = await PutAsync(client, world.TenantId, world.ConnectionId, Body(world, documentAttributeKey: "Cédula"), "\"1\"");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.PreconditionFailed), (saved.StatusCode, stale.StatusCode));
        // Sólo los nombres de los campos (la forma de tenancy.settings.updated); el número de cuenta no queda en audit.
        Assert.Equal(
            $"1|{world.Tenant.OwnerUserId}|{world.ConnectionId}|true|false",
            await ScalarAsync<string>(
                connectionString,
                """
                SELECT count(*) || '|' || max(actor_id::text) || '|' || max(resource_id)
                    || '|' || bool_and(jsonb_typeof(changed_fields) = 'array' AND changed_fields ? 'advisorMembershipId' AND changed_fields ? 'billingAccountNumber')
                    || '|' || bool_or(changed_fields::text LIKE '%' || @account || '%')
                FROM audit.entries WHERE action = 'shopify.store.updated' AND tenant_id = @t
                """,
                ("t", world.TenantId),
                ("account", world.Company.AccountNumber)));
    }

    // §6.1: si el PUT es el primer contacto, crea la tienda con el dominio de la conexión y la ventana de la
    // conciliación en el instante del enlace (la creación de la conexión), no en «ahora».
    [Fact]
    public async Task StoresPutWithoutStoreCreatesItWithShopDomainAndLinkedAt()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await ExecuteAsync(
            connectionString,
            "UPDATE integrations.connections SET created_at = '2026-10-01T15:00:00Z' WHERE id = @c",
            ("c", world.ConnectionId));
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, IntegrationsPermissions.ConnectionManage);

        var response = await PutAsync(client, world.TenantId, world.ConnectionId, Body(world), "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $"{Shop}|2026-10-01 15:00:00|2",
            await ScalarAsync<string>(
                connectionString,
                "SELECT shop_domain || '|' || to_char(last_reconciled_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS') || '|' || version FROM shopify.stores WHERE connection_id = @c",
                ("c", world.ConnectionId)));
    }

    // Borrar la conexión y volver a enlazar la tienda (D-S23) deja las filas del espejo con la conexión vieja: el reintento resuelve
    // la conexión nueva y daba not_operational, y la conciliación sólo reintenta las de su conexión. Cuando nace la tienda de la
    // conexión nueva, las filas Waiting del mismo tenant y dominio pasan a ella. Las terminales y las de otro tenant no se tocan.
    [Fact]
    public async Task StoresPutOfARelinkedStoreAdoptsTheWaitingRowsOfTheOldConnection()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var (oldConnection, otherTenant, otherConnection) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await ShopifyInboxSeed.StoreAsync(factory, world.TenantId, oldConnection, Shop);
        await ShopifyInboxSeed.StoreAsync(factory, otherTenant, otherConnection, Shop);
        var waiting = await ShopifyInboxSeed.WaitingAsync(factory, world.TenantId, oldConnection, 5_550_000_000_201L);
        var ignored = await ShopifyInboxSeed.IgnoredAsync(factory, world.TenantId, oldConnection, 5_550_000_000_202L);
        var foreign = await ShopifyInboxSeed.WaitingAsync(factory, otherTenant, otherConnection, 5_550_000_000_203L);
        using var client = CreateClient(
            factory, world.Tenant.OwnerUserId, world.TenantId, IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage);

        var response = await PutAsync(client, world.TenantId, world.ConnectionId, Body(world), "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            (world.ConnectionId, oldConnection, otherConnection),
            (await ConnectionOfAsync(connectionString, waiting), await ConnectionOfAsync(connectionString, ignored),
                await ConnectionOfAsync(connectionString, foreign)));
        var stores = await client.GetFromJsonAsync<JsonArray>(StoresRoute(world.TenantId), Ct);
        Assert.Equal(1, Assert.Single(stores!)!["waitingCount"]!.GetValue<int>());
    }

    private static Task<Guid> ConnectionOfAsync(string connectionString, Guid rowId) =>
        ScalarAsync<Guid>(connectionString, "SELECT connection_id FROM shopify.orders WHERE id = @id", ("id", rowId));

    [Fact]
    public async Task StoresPutEmptyAttributeKeyReturns422WithErrors()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, IntegrationsPermissions.ConnectionManage);

        var response = await PutAsync(client, world.TenantId, world.ConnectionId, Body(world, documentAttributeKey: " "), "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Ct);
        Assert.Equal("validation.failed", Str(problem!, "code"));
        Assert.NotNull(problem!["errors"]!["documentAttributeKey"]);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.stores"));
    }

    [Fact]
    public async Task StoresPutAdvisorNotMemberReturns422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, IntegrationsPermissions.ConnectionManage);
        var body = Body(world);
        body["advisorMembershipId"] = Guid.CreateVersion7();

        var response = await PutAsync(client, world.TenantId, world.ConnectionId, body, "\"1\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ShopifyErrorCodes.StoreAdvisorNotMember, await CodeAsync(response));
    }

    // Sin la política, RequireAuthorization no resuelve y el síntoma sería 500 (CLAUDE.md): un 403 dice que existe.
    [Fact]
    public async Task StoresWithoutPermissionReturn403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var reader = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, IntegrationsPermissions.ConnectionRead);
        using var nobody = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, ShopifyPermissions.OrderRead);

        var list = await nobody.GetAsync(StoresRoute(world.TenantId), Ct);
        var get = await nobody.GetAsync(StoreRoute(world.TenantId, world.ConnectionId), Ct);
        var put = await PutAsync(reader, world.TenantId, world.ConnectionId, Body(world), "\"1\"");

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { list.StatusCode, get.StatusCode, put.StatusCode });
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.stores"));
    }

    [Fact]
    public async Task StoresModuleOffReturns403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await ExecuteAsync(
            connectionString,
            "UPDATE tenancy.tenant_modules SET status = 'inactive', status_changed_at = now() WHERE tenant_id = @t AND module_key = 'shopify'",
            ("t", world.TenantId));
        using var client = CreateClient(
            factory, world.Tenant.OwnerUserId, world.TenantId, IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage);

        var list = await client.GetAsync(StoresRoute(world.TenantId), Ct);
        var get = await client.GetAsync(StoreRoute(world.TenantId, world.ConnectionId), Ct);
        var put = await PutAsync(client, world.TenantId, world.ConnectionId, Body(world), "\"1\"");

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { list.StatusCode, get.StatusCode, put.StatusCode });
        Assert.Equal("tenancy.module_not_enabled", await CodeAsync(get));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.stores"));
    }

    // La conexión es de otro tenant: 403, nunca 404, y no se crea nada.
    [Fact]
    public async Task StoresOtherTenantConnectionReturns403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var owner = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var intruder = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, intruder.TenantId);
        using var client = CreateClient(
            factory, intruder.OwnerUserId, intruder.TenantId, IntegrationsPermissions.ConnectionRead, IntegrationsPermissions.ConnectionManage);

        var foreign = await client.GetAsync(StoreRoute(intruder.TenantId, owner.ConnectionId), Ct);
        var otherRoute = await client.GetAsync(StoreRoute(owner.TenantId, owner.ConnectionId), Ct);
        var put = await PutAsync(client, intruder.TenantId, owner.ConnectionId, Body(owner), "\"1\"");

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { foreign.StatusCode, otherRoute.StatusCode, put.StatusCode });
        Assert.Equal("authorization.denied", await CodeAsync(foreign));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.stores"));
    }

    private static string StoresRoute(Guid tenantId) => $"/api/v1/tenants/{tenantId}/shopify/stores";

    private static string StoreRoute(Guid tenantId, Guid connectionId) => $"/api/v1/tenants/{tenantId}/shopify/stores/{connectionId}";

    private static JsonObject Body(ShopifyWorld world, string documentAttributeKey = "Documento") => new()
    {
        ["advisorMembershipId"] = world.AdvisorMembershipId,
        ["billingCompanyId"] = world.Company.Id,
        ["billingBankName"] = world.Company.BankName,
        ["billingAccountNumber"] = world.Company.AccountNumber,
        ["billingCurrency"] = "COP",
        ["defaultClassificationId"] = world.ClassificationId,
        ["documentAttributeKey"] = documentAttributeKey,
        ["documentTypeAttributeKey"] = "Tipo de documento",
        ["defaultIdentificationType"] = "nit",
    };

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid tenantId, Guid connectionId, JsonObject body, string ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, StoreRoute(tenantId, connectionId)) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request, Ct);
    }

    private static string? Str(JsonObject body, string key) => body[key]?.GetValue<string>();

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))?["code"]?.GetValue<string>();
}

/// <summary>Filas del espejo sembradas con el dominio y el snapshot de un fixture REST, como las deja la ingesta.</summary>
internal static class ShopifyInboxSeed
{
    public static async Task<ShopifyOrderSnapshot> SnapshotAsync(QepApiFactory factory, long orderId, string fixture = "taxes-included")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var parser = scope.ServiceProvider.GetRequiredService<IShopifyOrderPayloadParser>();
        var snapshot = parser.ParseRest(System.Text.Encoding.UTF8.GetString(OrderFixture(fixture)));
        return snapshot with { OrderId = orderId };
    }

    /// <summary>Una tienda vacía con su dominio, como la deja el primer contacto de una conexión que después se borró.</summary>
    public static async Task StoreAsync(QepApiFactory factory, Guid tenantId, Guid connectionId, string shopDomain)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var store = ShopifyStore.Create(connectionId, tenantId, DateTimeOffset.UtcNow.AddDays(-30));
        store.RecordShopDomain(shopDomain);
        scope.ServiceProvider.GetRequiredService<IShopifyStoreRepository>().Add(store);
        await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public static Task<Guid> WaitingAsync(QepApiFactory factory, Guid tenantId, Guid connectionId, long orderId) =>
        SeedAsync(factory, tenantId, connectionId, orderId, row => row.MarkWaiting(ShopifyWaitReasons.ProductUnmatched, """{"skus":[null]}""", DateTimeOffset.UtcNow));

    public static Task<Guid> IgnoredAsync(QepApiFactory factory, Guid tenantId, Guid connectionId, long orderId) =>
        SeedAsync(factory, tenantId, connectionId, orderId, row => row.MarkIgnored());

    public static Task<Guid> ImportedAsync(
        QepApiFactory factory, Guid tenantId, Guid connectionId, long orderId, string? warningsJson, DateTimeOffset? importedAt = null) =>
        SeedAsync(
            factory,
            tenantId,
            connectionId,
            orderId,
            row => row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, warningsJson, importedAt ?? DateTimeOffset.UtcNow));

    public static Task<Guid> ImportedWithErrorAsync(QepApiFactory factory, Guid tenantId, Guid connectionId, long orderId, string lastError) =>
        SeedAsync(
            factory,
            tenantId,
            connectionId,
            orderId,
            row =>
            {
                row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, DateTimeOffset.UtcNow);
                row.RecordLastError(lastError);
            });

    /// <summary>Anulada en QEP sin factura: <c>Cancelled</c> sin <c>wait_detail</c>.</summary>
    public static Task<Guid> CancelledAsync(QepApiFactory factory, Guid tenantId, Guid connectionId, long orderId) =>
        SeedAsync(
            factory,
            tenantId,
            connectionId,
            orderId,
            row =>
            {
                row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, null, DateTimeOffset.UtcNow.AddDays(-3));
                row.MarkCancelled(null);
            });

    public static Task<Guid> CancelledAfterInvoiceAsync(
        QepApiFactory factory, Guid tenantId, Guid connectionId, long orderId, string? warningsJson = null) =>
        SeedAsync(
            factory,
            tenantId,
            connectionId,
            orderId,
            row =>
            {
                row.MarkImported(Guid.CreateVersion7(), Guid.CreateVersion7(), 0m, warningsJson, DateTimeOffset.UtcNow.AddDays(-3));
                row.MarkCancelled(ShopifyOrderChanges.InvoicedBeforeCancelDetail);
            });

    private static async Task<Guid> SeedAsync(
        QepApiFactory factory, Guid tenantId, Guid connectionId, long orderId, Action<ShopifyOrderMirror> arrange)
    {
        var snapshot = await SnapshotAsync(factory, orderId);
        await using var scope = factory.Services.CreateAsyncScope();
        var row = ShopifyOrderMirror.Create(
            tenantId, connectionId, orderId, ShopifyOrderIngestion.WritePayload(snapshot), snapshot.UpdatedAt, snapshot.ToHeader());
        arrange(row);
        scope.ServiceProvider.GetRequiredService<IShopifyOrderMirrorRepository>().Add(row);
        await scope.ServiceProvider.GetRequiredService<IShopifyUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return row.Id;
    }
}
