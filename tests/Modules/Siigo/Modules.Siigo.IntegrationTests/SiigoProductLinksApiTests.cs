using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Modules.Siigo.Application;
using static Modules.Siigo.IntegrationTests.SiigoApiHarness;

namespace Modules.Siigo.IntegrationTests;

/// <summary>
/// Spec 2026-11-18 §5.3, paso 6: el vínculo entre las variantes de QEP y los productos de Siigo, de punta a punta con el host entero y los
/// adaptadores reales de Catalog e Integrations. La API de Siigo es el doble <see cref="FakeSiigoApi"/>. El vínculo es por variante; la
/// variante por defecto de un producto lleva el id del producto, así que el id que devuelve el alta es el de la variante.
/// </summary>
public sealed class SiigoProductLinksApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Route(Guid tenantId, Guid connectionId, string path) =>
        $"/api/v1/tenants/{tenantId}/siigo/connections/{connectionId}/{path}";

    private sealed record World(
        WebApplicationFactory<Program> Factory, RegisteredTenant Tenant, Guid ConnectionId, FakeSiigoApi Siigo, string ConnectionString, HttpClient Client)
        : IDisposable
    {
        public Guid TenantId => Tenant.TenantId;

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private static async Task<World> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var siigo = new FakeSiigoApi();
        var factory = new QepApiFactory(connectionString, siigo);
        var tenant = await RegisterTenantAsync(factory);
        await EnableSiigoAsync(connectionString, tenant.TenantId);
        var connectionId = await LinkSiigoConnectionAsync(factory, tenant.TenantId, "Siigo principal");
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, [SiigoPermissions.SettingsManage, .. SeedPermissions]);
        // La asociación de una variante exige un miembro activo: el dueño del tenant lo es.
        return new World(factory, tenant, connectionId, siigo, connectionString, client);
    }

    private static string SiigoProductsPage(int page, int pageSize, int total, params (string Id, string Code, string Name, bool Active)[] products) =>
        new JsonObject
        {
            ["pagination"] = new JsonObject { ["page"] = page, ["page_size"] = pageSize, ["total_results"] = total },
            ["results"] = new JsonArray([.. products.Select(product => (JsonNode)new JsonObject
            {
                ["id"] = product.Id,
                ["code"] = product.Code,
                ["name"] = product.Name,
                ["active"] = product.Active,
            })]),
        }.ToJsonString();

    private static async Task<JsonObject> GetProductsAsync(World world, string query = "")
    {
        var response = await world.Client.GetAsync(Route(world.TenantId, world.ConnectionId, $"products{query}"), Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    private static JsonObject RowOf(JsonObject page, Guid variantId) =>
        page["items"]!.AsArray().Select(item => item!.AsObject()).Single(item => item["variantId"]!.GetValue<string>() == variantId.ToString());

    private static async Task<long> SettingsVersionAsync(World world) =>
        (await world.Client.GetFromJsonAsync<JsonObject>(Route(world.TenantId, world.ConnectionId, "settings"), Ct))!["version"]!.GetValue<long>();

    [Fact]
    public async Task AutoLinkLinksOnlyUnlinkedVariantsToActiveProducts()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        var two = await CreateProductAsync(world.Client, world.TenantId, "AB-2", "Producto dos", 10_000m, null);
        var three = await CreateProductAsync(world.Client, world.TenantId, "AB-3", "Producto tres", 10_000m, null);
        world.Siigo.AddProduct("manual-3", "OTRO-3", "Elegido a mano");
        // AB-3 ya tiene un vínculo manual: el auto-vínculo no lo toca.
        (await world.Client.PutAsJsonAsync(
            Route(world.TenantId, world.ConnectionId, $"products/{three}/link"),
            new { siigoProductId = "manual-3", code = "OTRO-3", name = "Elegido a mano" },
            Ct)).EnsureSuccessStatusCode();
        // AB-2 casa por código con un producto de Siigo INACTIVO: no se vincula. «ab-1 » casa con AB-1 sin distinguir mayúsculas ni espacios.
        world.Siigo.On(
            "/v1/products",
            HttpStatusCode.OK,
            SiigoProductsPage(1, 100, 3, ("siigo-1", " ab-1 ", "Uno en Siigo", true), ("siigo-2", "AB-2", "Dos en Siigo", false), ("siigo-3", "AB-3", "Tres en Siigo", true)));
        var versionBefore = await SettingsVersionAsync(world);

        var response = await world.Client.PostAsync(Route(world.TenantId, world.ConnectionId, "products/auto-link"), content: null, Ct);
        await EnsureSuccessAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal((1, 0), (result["linked"]!.GetValue<int>(), result["skippedAmbiguous"]!.GetValue<int>()));
        var page = await GetProductsAsync(world);
        var rowOne = RowOf(page, one);
        Assert.Equal(("siigo-1", "AutoCode"), (rowOne["siigoProduct"]!["id"]!.GetValue<string>(), rowOne["origin"]!.GetValue<string>()));
        Assert.Null(RowOf(page, two)["siigoProduct"]);
        var rowThree = RowOf(page, three);
        Assert.Equal(("manual-3", "Manual"), (rowThree["siigoProduct"]!["id"]!.GetValue<string>(), rowThree["origin"]!.GetValue<string>()));
        // «Escribe en una sola transacción y sube Version una vez.»
        Assert.Equal(versionBefore + 1, await SettingsVersionAsync(world));
    }

    [Fact]
    public async Task AutoLinkMarksTheProductsStepEvenWithoutMatches()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.On("/v1/products", HttpStatusCode.OK, SiigoProductsPage(1, 100, 0));

        var response = await world.Client.PostAsync(Route(world.TenantId, world.ConnectionId, "products/auto-link"), content: null, Ct);
        await EnsureSuccessAsync(response);

        // La marca es la que evita que la pantalla vuelva a vincular sola al abrir el paso (spec 2026-11-20 §9).
        Assert.Equal(0, (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["linked"]!.GetValue<int>());
        var settings = (await world.Client.GetFromJsonAsync<JsonObject>(Route(world.TenantId, world.ConnectionId, "settings"), Ct))!;
        Assert.NotNull(settings["productsAutoLinkedAt"]);
    }

    [Fact]
    public async Task AutoLinkSkipsAmbiguousCodes()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        var two = await CreateProductAsync(world.Client, world.TenantId, "AB-2", "Producto dos", 10_000m, null);
        // Dos productos ACTIVOS de Siigo con el mismo código normalizado: no se sabe cuál es, no se vincula ninguno.
        world.Siigo.On(
            "/v1/products",
            HttpStatusCode.OK,
            SiigoProductsPage(1, 100, 3, ("siigo-a", "AB-1", "Uno A", true), ("siigo-b", "ab-1", "Uno B", true), ("siigo-2", "AB-2", "Dos", true)));

        var response = await world.Client.PostAsync(Route(world.TenantId, world.ConnectionId, "products/auto-link"), content: null, Ct);
        await EnsureSuccessAsync(response);

        var result = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal((1, 1), (result["linked"]!.GetValue<int>(), result["skippedAmbiguous"]!.GetValue<int>()));
        var page = await GetProductsAsync(world);
        Assert.Null(RowOf(page, one)["siigoProduct"]);
        Assert.Equal("siigo-2", RowOf(page, two)["siigoProduct"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task AutoLinkWalksEveryPageOfSiigo()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        var two = await CreateProductAsync(world.Client, world.TenantId, "AB-2", "Producto dos", 10_000m, null);
        // 101 resultados de a 100: la segunda página existe.
        world.Siigo.OnRequest("/v1/products", call => FakeSiigoApi.Respond(
            HttpStatusCode.OK,
            call.Query.Contains("page=2", StringComparison.Ordinal)
                ? SiigoProductsPage(2, 100, 101, ("siigo-2", "AB-2", "Dos", true))
                : SiigoProductsPage(1, 100, 101, ("siigo-1", "AB-1", "Uno", true))));

        var response = await world.Client.PostAsync(Route(world.TenantId, world.ConnectionId, "products/auto-link"), content: null, Ct);
        await EnsureSuccessAsync(response);

        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["linked"]!.GetValue<int>());
        Assert.Equal(2, world.Siigo.CallsTo("/v1/products"));
        var page = await GetProductsAsync(world);
        Assert.Equal(["siigo-1", "siigo-2"], new[] { RowOf(page, one), RowOf(page, two) }.Select(row => row["siigoProduct"]!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ManualLinkReplacesAnAutoLink()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        world.Siigo.On(
            "/v1/products", HttpStatusCode.OK, SiigoProductsPage(1, 100, 2, ("siigo-auto", "AB-1", "Automático", true), ("siigo-manual", "ZZ-9", "Elegido a mano", true)));
        (await world.Client.PostAsync(Route(world.TenantId, world.ConnectionId, "products/auto-link"), content: null, Ct)).EnsureSuccessStatusCode();
        Assert.Equal("AutoCode", RowOf(await GetProductsAsync(world), one)["origin"]!.GetValue<string>());

        var manual = await world.Client.PutAsJsonAsync(
            Route(world.TenantId, world.ConnectionId, $"products/{one}/link"),
            new { siigoProductId = "siigo-manual", code = "ZZ-9", name = "Nombre del cliente" },
            Ct);

        Assert.Equal(HttpStatusCode.Created, manual.StatusCode);
        var row = RowOf(await GetProductsAsync(world), one);
        Assert.Equal(("siigo-manual", "ZZ-9", "Elegido a mano", "Manual"), (
            row["siigoProduct"]!["id"]!.GetValue<string>(),
            row["siigoProduct"]!["code"]!.GetValue<string>(),
            row["siigoProduct"]!["name"]!.GetValue<string>(),
            row["origin"]!.GetValue<string>()));
        Assert.Equal(1L, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.variant_links WHERE variant_id = @id", ("id", one)));
    }

    [Fact]
    public async Task UnlinkingRemovesTheLinkAndLinkingAnUnknownVariantIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        world.Siigo.AddProduct("s-1", "AB-1", "Uno");
        (await world.Client.PutAsJsonAsync(
            Route(world.TenantId, world.ConnectionId, $"products/{one}/link"), new { siigoProductId = "s-1", code = "AB-1", name = "Uno" }, Ct)).EnsureSuccessStatusCode();

        var removed = await world.Client.DeleteAsync(Route(world.TenantId, world.ConnectionId, $"products/{one}/link"), Ct);
        var unknown = await world.Client.PutAsJsonAsync(
            Route(world.TenantId, world.ConnectionId, $"products/{Guid.CreateVersion7()}/link"), new { siigoProductId = "s-9", code = "X", name = "X" }, Ct);

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Null(RowOf(await GetProductsAsync(world), one)["siigoProduct"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal("siigo.product.variant_not_found", (await unknown.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task CoverageCountsLinkedOverActiveVariants()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        var two = await CreateProductAsync(world.Client, world.TenantId, "AB-2", "Producto dos", 10_000m, null);
        await CreateProductAsync(world.Client, world.TenantId, "AB-3", "Producto tres", 10_000m, null);
        foreach (var variant in new[] { one, two })
        {
            world.Siigo.AddProduct($"s-{variant:N}", "C", "N");
            (await world.Client.PutAsJsonAsync(
                Route(world.TenantId, world.ConnectionId, $"products/{variant}/link"), new { siigoProductId = $"s-{variant:N}", code = "C", name = "N" }, Ct)).EnsureSuccessStatusCode();
        }

        var before = (await GetProductsAsync(world))["coverage"]!;
        Assert.Equal((2, 3), (before["linked"]!.GetValue<int>(), before["totalActive"]!.GetValue<int>()));

        // El producto vinculado que se desactiva deja de contar en las dos mitades: la cobertura es sobre variantes activas.
        await ExecuteAsync(world.ConnectionString, "UPDATE catalog.products SET is_active = false WHERE id = @id", ("id", two));
        var after = (await GetProductsAsync(world))["coverage"]!;
        Assert.Equal((1, 2), (after["linked"]!.GetValue<int>(), after["totalActive"]!.GetValue<int>()));
    }

    [Fact]
    public async Task TheUnlinkedFilterShowsOnlyVariantsWithoutLink()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var linked = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        var free = await CreateProductAsync(world.Client, world.TenantId, "AB-2", "Producto dos", 10_000m, null);
        world.Siigo.AddProduct("s-1", "AB-1", "Uno");
        (await world.Client.PutAsJsonAsync(
            Route(world.TenantId, world.ConnectionId, $"products/{linked}/link"), new { siigoProductId = "s-1", code = "AB-1", name = "Uno" }, Ct)).EnsureSuccessStatusCode();

        var all = await GetProductsAsync(world, "?filter=all");
        var unlinked = await GetProductsAsync(world, "?filter=unlinked");
        var onlyLinked = await GetProductsAsync(world, "?filter=linked");

        Assert.Equal(2, all["total"]!.GetValue<int>());
        Assert.Equal([free.ToString()], unlinked["items"]!.AsArray().Select(item => item!["variantId"]!.GetValue<string>()));
        Assert.Equal(1, unlinked["total"]!.GetValue<int>());
        Assert.Equal([linked.ToString()], onlyLinked["items"]!.AsArray().Select(item => item!["variantId"]!.GetValue<string>()));
        // Cada fila trae lo que la tabla pinta.
        var row = RowOf(all, linked);
        Assert.Equal(("Producto uno", "AB-1", true), (row["displayName"]!.GetValue<string>(), row["code"]!.GetValue<string>(), row["isActive"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task TheListSearchesByCodeOrName()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Cuaderno rayado", 10_000m, null);
        await CreateProductAsync(world.Client, world.TenantId, "ZZ-9", "Lápiz", 10_000m, null);

        var byCode = await GetProductsAsync(world, "?search=ab-");
        var byName = await GetProductsAsync(world, "?search=CUADERNO");

        Assert.Equal([one.ToString()], byCode["items"]!.AsArray().Select(item => item!["variantId"]!.GetValue<string>()));
        Assert.Equal([one.ToString()], byName["items"]!.AsArray().Select(item => item!["variantId"]!.GetValue<string>()));
        Assert.Equal((1, 50), (byCode["page"]!.GetValue<int>(), byCode["pageSize"]!.GetValue<int>()));
    }

    [Fact]
    public async Task AnInvalidFilterIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var response = await world.Client.GetAsync(Route(world.TenantId, world.ConnectionId, "products?filter=everything"), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("filter", (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["errors"]!.AsObject().Select(pair => pair.Key));
    }

    private static async Task<JsonObject> GetSiigoProductsAsync(World world, string query = "")
    {
        var response = await world.Client.GetAsync(Route(world.TenantId, world.ConnectionId, $"siigo-products{query}"), Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }

    private static string[] CodesOf(JsonObject page) => [.. page["items"]!.AsArray().Select(item => item!["code"]!.GetValue<string>())];

    // El selector de la modal «Vincular»: los productos activos de Siigo, por código o nombre, y `refresh` para lo recién creado.
    [Fact]
    public async Task ThePickerListsActiveSiigoProductsAndSearchesByCodeOrName()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.AddProduct("s-1", "AB-1", "Camiseta básica");
        world.Siigo.AddProduct("s-2", "AB-2", "Pantalón largo");
        world.Siigo.AddProduct("s-3", "AB-3", "Producto inactivo", active: false);

        var all = await GetSiigoProductsAsync(world);
        var byName = await GetSiigoProductsAsync(world, "?search=pantalon");
        var byCode = await GetSiigoProductsAsync(world, "?search=ab-1");

        Assert.Equal(["AB-1", "AB-2"], CodesOf(all));
        Assert.Equal((1, 20, 2), (all["page"]!.GetValue<int>(), all["pageSize"]!.GetValue<int>(), all["total"]!.GetValue<int>()));
        Assert.Equal(["AB-2"], CodesOf(byName));
        Assert.Equal(["AB-1"], CodesOf(byCode));
        Assert.Equal("s-1", all["items"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task ThePickerKeepsACopyUntilRefreshAndRefreshShowsWhatWasJustCreated()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.AddProduct("s-1", "AB-1", "Uno");
        Assert.Equal(["AB-1"], CodesOf(await GetSiigoProductsAsync(world)));

        world.Siigo.AddProduct("s-2", "NEW-2", "Recién creado en Siigo");

        Assert.Equal(["AB-1"], CodesOf(await GetSiigoProductsAsync(world)));
        Assert.Equal(["AB-1", "NEW-2"], CodesOf(await GetSiigoProductsAsync(world, "?refresh=true")));
        Assert.Equal(["AB-1", "NEW-2"], CodesOf(await GetSiigoProductsAsync(world)));
    }

    [Fact]
    public async Task ThePickerWithAnUnusableConnectionIs409AndAnInvalidPageIs422()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);

        var invalid = await world.Client.GetAsync(Route(world.TenantId, world.ConnectionId, "siigo-products?page=0"), Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        world.Siigo.On("/auth", HttpStatusCode.Unauthorized, "{}");
        var unusable = await world.Client.GetAsync(Route(world.TenantId, world.ConnectionId, "siigo-products?refresh=true"), Ct);
        Assert.Equal(HttpStatusCode.Conflict, unusable.StatusCode);
    }

    [Fact]
    public async Task AutoLinkWithAnUnusableConnectionIs409()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        world.Siigo.On("/auth", HttpStatusCode.Unauthorized, "{}");

        var response = await world.Client.PostAsync(Route(world.TenantId, world.ConnectionId, "products/auto-link"), content: null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("siigo.connection_unavailable", (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>());
    }

    // Review I-1: el vínculo manual se valida contra Siigo. Un código viejo o mal escrito daría un 4xx al facturar, y esos fallos cuentan
    // para el bloqueo del 80 % de Siigo.
    [Fact]
    public async Task ALinkToACodeSiigoDoesNotHaveIs422AndCreatesNoRow()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        world.Siigo.AddProduct("s-real", "AB-REAL", "Real");

        var response = await world.Client.PutAsJsonAsync(
            Route(world.TenantId, world.ConnectionId, $"products/{one}/link"), new { siigoProductId = "s-real", code = "AB-TYPO", name = "Real" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("siigo.product.not_found_in_siigo", (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["code"]!.GetValue<string>());
        Assert.Equal(0L, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.variant_links"));
    }

    [Theory]
    [InlineData("other-id", true)]
    [InlineData("s-1", false)]
    public async Task ALinkWithAnotherIdOrAnInactiveSiigoProductIs422(string sentId, bool active)
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        world.Siigo.AddProduct("s-1", "AB-1", "Uno", active);

        var response = await world.Client.PutAsJsonAsync(
            Route(world.TenantId, world.ConnectionId, $"products/{one}/link"), new { siigoProductId = sentId, code = "AB-1", name = "Uno" }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0L, await ScalarAsync<long>(world.ConnectionString, "SELECT count(*) FROM siigo.variant_links"));
    }

    [Fact]
    public async Task AValidLinkIs201AndTakesCodeAndNameFromSiigo()
    {
        await using var database = await StartDatabaseAsync();
        using var world = await ArrangeAsync(database);
        var one = await CreateProductAsync(world.Client, world.TenantId, "AB-1", "Producto uno", 10_000m, null);
        world.Siigo.AddProduct("s-1", "ab-1", "Nombre en Siigo");

        var response = await world.Client.PutAsJsonAsync(
            Route(world.TenantId, world.ConnectionId, $"products/{one}/link"), new { siigoProductId = "s-1", code = "ab-1", name = "Nombre del cliente" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var row = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal(("s-1", "ab-1", "Nombre en Siigo", "Manual"), (
            row["siigoProduct"]!["id"]!.GetValue<string>(),
            row["siigoProduct"]!["code"]!.GetValue<string>(),
            row["siigoProduct"]!["name"]!.GetValue<string>(),
            row["origin"]!.GetValue<string>()));
    }
}
