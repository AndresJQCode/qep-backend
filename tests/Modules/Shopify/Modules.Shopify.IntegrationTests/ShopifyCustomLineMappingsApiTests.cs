using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modules.Quotations.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-13: <c>GET</c>/<c>POST</c>/<c>DELETE</c> de las asociaciones de líneas personalizadas de una tienda, de punta a
/// punta con el host entero (políticas, validador, mapeo de errores) contra Postgres y con los permisos reales de Quotations.
/// </summary>
public sealed class ShopifyCustomLineMappingsApiTests
{
    private const string Garantia = "Garantía por 1 año + Envío Prioritario🚛";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PostCreatesListsAndDeletes()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);

        var created = await client.PostAsJsonAsync(Route(world), new { title = "  " + Garantia + " ", variantId = world.Gorra }, Ct);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var row = (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        var id = row["id"]!.GetValue<Guid>();
        var dto = row.Deserialize<ShopifyCustomLineMappingDto>(JsonSerializerOptions.Web)!;
        Assert.Equal((id, Garantia, world.Gorra, true), (dto.Id, dto.Title, dto.VariantId, dto.IsActive));
        Assert.Equal($"{Route(world)}/{id}", created.Headers.Location?.ToString());
        Assert.Equal(
            (Garantia, world.Gorra.ToString(), "Gorra", "GOR-002", true),
            (Str(row, "title"), Str(row, "variantId"), Str(row, "productName"), Str(row, "variantCode"), row["isActive"]!.GetValue<bool>()));

        var list = (await client.GetFromJsonAsync<JsonArray>(Route(world), Ct))!;
        var listed = Assert.Single(list)!.AsObject();
        Assert.True(JsonNode.DeepEquals(row, listed));

        var deleted = await client.DeleteAsync($"{Route(world)}/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonArray>(Route(world), Ct))!);

        Assert.Equal(
            "shopify.custom_line_mapping.created|shopify.custom_line_mapping.deleted",
            await ScalarAsync<string>(
                connectionString,
                "SELECT string_agg(action, '|' ORDER BY occurred_at) FROM audit.entries WHERE tenant_id = @t AND action LIKE 'shopify.custom_line_mapping.%'",
                ("t", world.TenantId)));
        Assert.Equal(
            $"shopify_custom_line_mapping|{id}|{world.ConnectionId}",
            await ScalarAsync<string>(
                connectionString,
                "SELECT resource_type || '|' || resource_id || '|' || (changed_fields ->> 'connectionId') FROM audit.entries WHERE action = 'shopify.custom_line_mapping.created' AND tenant_id = @t",
                ("t", world.TenantId)));
    }

    [Fact]
    public async Task GetIsOrderedByTitleOrdinal()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);
        foreach (var title in new[] { "b", "B", "a" })
        {
            var response = await client.PostAsJsonAsync(Route(world), new { title, variantId = world.Gorra }, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var list = (await client.GetFromJsonAsync<JsonArray>(Route(world), Ct))!;

        Assert.Equal(["B", "a", "b"], list.Select(item => Str(item!.AsObject(), "title")));
    }

    // D5: la variante no tiene FK. Si Catalog la borró, la fila sigue a la vista (para poder quitarla) con los textos en null y
    // sin poder venderse; el JSON trae las llaves en null, no las omite.
    [Fact]
    public async Task GetShowsADeletedVariantAsNullWithInactive()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var gone = Guid.CreateVersion7();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO shopify.custom_line_mappings (id, tenant_id, connection_id, title, variant_id, created_at, created_by)
            VALUES (@id, @t, @c, 'Variante borrada', @v, now(), @by)
            """,
            ("id", Guid.CreateVersion7()), ("t", world.TenantId), ("c", world.ConnectionId), ("v", gone), ("by", world.AdvisorMembershipId));
        using var client = ClientOf(factory, world);

        var list = (await client.GetFromJsonAsync<JsonArray>(Route(world), Ct))!;

        var row = Assert.Single(list)!.AsObject();
        Assert.Equal(gone.ToString(), Str(row, "variantId"));
        Assert.True(row.ContainsKey("productName") && row["productName"] is null);
        Assert.True(row.ContainsKey("variantCode") && row["variantCode"] is null);
        Assert.False(row["isActive"]!.GetValue<bool>());
    }

    // Review Focus 4: la violación del índice sale 409 con su código, y el título recortado es la clave.
    [Fact]
    public async Task PostTheSameTitleTwiceIs409()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);
        var first = await client.PostAsJsonAsync(Route(world), new { title = "Garantía", variantId = world.Gorra }, Ct);

        var second = await client.PostAsJsonAsync(Route(world), new { title = "  Garantía ", variantId = world.Camiseta }, Ct);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Conflict), (first.StatusCode, second.StatusCode));
        Assert.Equal(ShopifyErrorCodes.CustomLineAlreadyMapped, await CodeAsync(second));
        // El que pierde no deja asociación ni auditoría.
        Assert.Equal(
            "1|1",
            await ScalarAsync<string>(
                connectionString,
                """
                SELECT (SELECT count(*) FROM shopify.custom_line_mappings) || '|'
                    || (SELECT count(*) FROM audit.entries WHERE action = 'shopify.custom_line_mapping.created' AND tenant_id = @t)
                """,
                ("t", world.TenantId)));
    }

    [Fact]
    public async Task PostWithAnUnknownVariantIs422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);

        var response = await client.PostAsJsonAsync(Route(world), new { title = "Garantía", variantId = Guid.CreateVersion7() }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ShopifyErrorCodes.CustomLineVariantNotFound, await CodeAsync(response));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.custom_line_mappings"));
    }

    [Fact]
    public async Task PostWithAnInactiveVariantIs422()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        await ExecuteAsync(connectionString, "UPDATE catalog.products SET is_active = false WHERE id = @id", ("id", world.Gorra));
        using var client = ClientOf(factory, world);

        var response = await client.PostAsJsonAsync(Route(world), new { title = "Garantía", variantId = world.Gorra }, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(ShopifyErrorCodes.CustomLineVariantInactive, await CodeAsync(response));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.custom_line_mappings"));
    }

    [Theory]
    [InlineData("""{"title":"   ","variantId":"GORRA"}""", "title")]
    [InlineData("""{"variantId":"GORRA"}""", "title")]
    [InlineData("""{"title":"x"}""", "variantId")]
    [InlineData("""{"title":"x","variantId":"00000000-0000-0000-0000-000000000000"}""", "variantId")]
    public async Task PostWithoutTitleOrVariantIs422WithTheField(string body, string field)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);

        var response = await PostRawAsync(client, Route(world), body.Replace("GORRA", world.Gorra.ToString(), StringComparison.Ordinal));

        await AssertValidationAsync(response, field);
    }

    // El límite de 255 se mide con la misma regla que el dominio (Length tras Trim, unidades UTF-16): un título de 255 con emojis
    // entra (cada 🚛 cuenta 2) y uno de 256 da 422 con el campo, no 500 por la `ArgumentException` del dominio.
    [Fact]
    public async Task TheTitleLimitIs255CharsAfterTrimWithEmojiAtTheBoundary()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);
        var atTheLimit = string.Concat(Enumerable.Repeat("🚛", 127)) + "a";
        var over = atTheLimit + "b";
        Assert.Equal((255, 256), (atTheLimit.Length, over.Length));

        var accepted = await client.PostAsJsonAsync(Route(world), new { title = $"  {atTheLimit}  ", variantId = world.Gorra }, Ct);
        var rejected = await client.PostAsJsonAsync(Route(world), new { title = over, variantId = world.Gorra }, Ct);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(atTheLimit, Str((await accepted.Content.ReadFromJsonAsync<JsonObject>(Ct))!, "title"));
        await AssertValidationAsync(rejected, "title");
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.custom_line_mappings"));
    }

    [Fact]
    public async Task AnotherTenantGets403OnEveryVerb()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var owner = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var intruder = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, intruder.TenantId);
        using var client = CreateClient(
            factory, intruder.OwnerUserId, intruder.TenantId, OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);

        var get = await client.GetAsync(Route(owner), Ct);
        var post = await client.PostAsJsonAsync(Route(owner), new { title = "x", variantId = owner.Gorra }, Ct);
        var delete = await client.DeleteAsync($"{Route(owner)}/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { get.StatusCode, post.StatusCode, delete.StatusCode });
        Assert.Equal("authorization.denied|authorization.denied|authorization.denied", $"{await CodeAsync(get)}|{await CodeAsync(post)}|{await CodeAsync(delete)}");
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.custom_line_mappings"));
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIs403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var owner = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var intruder = await RegisterTenantAsync(factory);
        await EnableShopifyAsync(connectionString, intruder.TenantId);
        using var client = CreateClient(
            factory, intruder.OwnerUserId, intruder.TenantId, OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var foreignRoute = $"/api/v1/tenants/{intruder.TenantId}/shopify/stores/{owner.ConnectionId}/custom-line-mappings";

        var get = await client.GetAsync(foreignRoute, Ct);
        var post = await client.PostAsJsonAsync(foreignRoute, new { title = "x", variantId = owner.Gorra }, Ct);
        var delete = await client.DeleteAsync($"{foreignRoute}/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(
            [HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { get.StatusCode, post.StatusCode, delete.StatusCode });
        Assert.Equal("authorization.denied", await CodeAsync(get));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.custom_line_mappings"));
    }

    [Fact]
    public async Task WithoutOrderManagePostAndDeleteAre403()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var reader = CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead);

        var get = await reader.GetAsync(Route(world), Ct);
        var post = await reader.PostAsJsonAsync(Route(world), new { title = "x", variantId = world.Gorra }, Ct);
        var delete = await reader.DeleteAsync($"{Route(world)}/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden],
            new[] { get.StatusCode, post.StatusCode, delete.StatusCode });
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.custom_line_mappings"));
    }

    // D7: borrar algo que no existe es idempotente y no deja auditoría.
    [Fact]
    public async Task DeletingAnUnknownIdIs204AndAuditsNothing()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        using var client = ClientOf(factory, world);

        var response = await client.DeleteAsync($"{Route(world)}/{Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connectionString,
                "SELECT count(*) FROM audit.entries WHERE action = 'shopify.custom_line_mapping.deleted' AND tenant_id = @t",
                ("t", world.TenantId)));
    }

    private static HttpClient ClientOf(QepApiFactory factory, ShopifyWorld world) =>
        CreateClient(factory, world.Tenant.OwnerUserId, world.TenantId, OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);

    private static string Route(ShopifyWorld world) =>
        $"/api/v1/tenants/{world.TenantId}/shopify/stores/{world.ConnectionId}/custom-line-mappings";

    private static Task<HttpResponseMessage> PostRawAsync(HttpClient client, string route, string json) =>
        client.PostAsync(route, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static async Task AssertValidationAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        Assert.Equal("validation.failed", Str(problem, "code"));
        Assert.NotNull(problem["errors"]![field]);
    }

    private static string? Str(JsonObject body, string key) => body[key]?.GetValue<string>();

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))?["code"]?.GetValue<string>();
}
