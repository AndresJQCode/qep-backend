using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec qep-frontend 2026-10-15 (bandeja: filtro por etiqueta, sólo no leídas y orden): tres parámetros nuevos en
/// <c>GET /conversations</c> y los chips de etiqueta del cliente en cada fila. Los filtros van en el servidor porque la lista es
/// paginada: filtrar en el cliente sólo vería la página cargada. Aplican a las dos pestañas y no cambian los contadores.</summary>
public sealed class ConversationListFiltersApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions));
    }

    /// <summary>Un entrante crea la conversación y su cliente (la ingesta lo asegura), con su hora de actividad.</summary>
    private static async Task<(Guid Conversation, Guid Customer)> IngestAsync(Fixture f, string userId, string wamid, long timestamp, string profileName)
    {
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", userId, null, wamid, timestamp, "hola", profileName: profileName));
        await DrainDeliveriesAsync(f.Factory);
        var conversation = await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations WHERE user_id = @u", ("u", userId));
        var customer = await ScalarAsync<Guid>(f.ConnectionString, "SELECT customer_id FROM messaging.conversations WHERE id = @id", ("id", conversation));
        return (conversation, customer);
    }

    private static async Task<Guid> TagAsync(Fixture f, string name, string color, params Guid[] customers)
    {
        var tagId = Guid.CreateVersion7();
        await ExecuteAsync(f.ConnectionString,
            "INSERT INTO customers.tags (id, tenant_id, name, color, version, created_at, updated_at) VALUES (@id, @t, @n, @c, 1, now(), now())",
            ("id", tagId), ("t", f.Tenant.TenantId), ("n", name), ("c", color));
        foreach (var customer in customers)
        {
            await ExecuteAsync(f.ConnectionString,
                "INSERT INTO customers.customer_tags (customer_id, tag_id, tenant_id, assigned_at, assigned_by_user_id) VALUES (@cu, @tg, @t, now(), @u)",
                ("cu", customer), ("tg", tagId), ("t", f.Tenant.TenantId), ("u", f.Tenant.OwnerUserId));
        }

        return tagId;
    }

    private static Task<JsonElement> ListAsync(Fixture f, string query) =>
        f.Client.GetFromJsonAsync<JsonElement>($"{ConversationsUrl(f.Tenant.TenantId)}{query}", Ct);

    private static Guid[] Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    // Spec 2026-10-21 §4.3: el filtro `automation` y counts.automated. Se activa por SQL: el helper del harness llega después.
    [Fact]
    public async Task AutomationFilterAndCountNeedTheHarnessToActivate()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (active, _) = await IngestAsync(f, "CO.A", "w1", 1760000100, "Ana");
        var (plain, _) = await IngestAsync(f, "CO.B", "w2", 1760000200, "Beto");
        await ExecuteAsync(f.ConnectionString,
            "UPDATE messaging.conversations SET automation_status = 'Active', automation_started_at = now() WHERE id = @id", ("id", active));

        var all = await ListAsync(f, "");
        Assert.Equal([plain, active], Ids(all));
        Assert.Equal(1, all.GetProperty("counts").GetProperty("automated").GetInt32());
        var items = all.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetGuid());
        Assert.Equal("Active", items[active].GetProperty("automation").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, items[plain].GetProperty("automation").ValueKind);

        Assert.Equal([active], Ids(await ListAsync(f, "?automation=active")));
        Assert.Equal([active], Ids(await ListAsync(f, "?automation=any")));
        Assert.Equal([plain], Ids(await ListAsync(f, "?automation=none")));
        Assert.Empty(Ids(await ListAsync(f, "?automation=paused")));

        var invalid = await f.Client.GetAsync($"{ConversationsUrl(f.Tenant.TenantId)}?automation=bot", Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal("validation.failed", (await invalid.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheTagFilterKeepsOnlyConversationsOfCustomersWithThatTagAndTheCountsDoNotMove()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (laura, lauraCustomer) = await IngestAsync(f, "CO.L", "w1", 1760000100, "Laura");
        var (mateo, _) = await IngestAsync(f, "CO.M", "w2", 1760000200, "Mateo");
        var (sofia, sofiaCustomer) = await IngestAsync(f, "CO.S", "w3", 1760000300, "Sofía");
        var vip = await TagAsync(f, "VIP", "purple", lauraCustomer, sofiaCustomer);
        var empty = await TagAsync(f, "Mayorista", "teal");

        var all = await ListAsync(f, "");
        var tagged = await ListAsync(f, $"?tagId={vip}");
        var none = await ListAsync(f, $"?tagId={empty}");
        var foreign = await ListAsync(f, $"?tagId={Guid.CreateVersion7()}");

        Assert.Equal([sofia, mateo, laura], Ids(all));
        Assert.Equal([sofia, laura], Ids(tagged));
        Assert.Empty(Ids(none));
        Assert.Empty(Ids(foreign));
        // §5.4: los contadores cuentan el tenant entero, sin filtros.
        Assert.Equal(all.GetProperty("counts").GetRawText(), tagged.GetProperty("counts").GetRawText());
    }

    [Fact]
    public async Task UnreadKeepsOnlyConversationsWithUnreadMessagesInBothTabs()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (read, _) = await IngestAsync(f, "CO.R", "w1", 1760000100, "Ana");
        var (unread, _) = await IngestAsync(f, "CO.U", "w2", 1760000200, "Beto");
        var (resolvedUnread, _) = await IngestAsync(f, "CO.X", "w3", 1760000300, "Carla");
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET unread_count = 0 WHERE id = @id", ("id", read));
        await ExecuteAsync(f.ConnectionString, "UPDATE messaging.conversations SET status = 'Resolved' WHERE id = @id", ("id", resolvedUnread));

        Assert.Equal([unread], Ids(await ListAsync(f, "?unread=true")));
        Assert.Equal([resolvedUnread], Ids(await ListAsync(f, "?status=Resolved&unread=true")));
        // false equivale a ausente.
        Assert.Equal([unread, read], Ids(await ListAsync(f, "?unread=false")));
    }

    [Fact]
    public async Task OldestFirstReversesTheOrderAndPagesTheSameWay()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (first, _) = await IngestAsync(f, "CO.1", "w1", 1760000100, "Uno");
        var (second, _) = await IngestAsync(f, "CO.2", "w2", 1760000200, "Dos");
        var (third, _) = await IngestAsync(f, "CO.3", "w3", 1760000300, "Tres");

        Assert.Equal([third, second, first], Ids(await ListAsync(f, "?sort=newest")));
        Assert.Equal([first, second, third], Ids(await ListAsync(f, "?sort=oldest")));
        var oldestFirstPage = await ListAsync(f, "?sort=oldest&pageSize=2");
        Assert.Equal([first, second], Ids(oldestFirstPage));
        var oldestSecondPage = await ListAsync(f, $"?sort=oldest&pageSize=2&cursor={NextCursor(oldestFirstPage)}");
        Assert.Equal([third], Ids(oldestSecondPage));
        Assert.Equal(JsonValueKind.Null, oldestSecondPage.GetProperty("nextCursor").ValueKind);
    }

    /// <summary>Scroll infinito de la bandeja: el cursor es la posición <c>(last_activity_at, id)</c> de la última fila, así que una
    /// conversación que sube al tope entre una página y la siguiente no empuja otra a la página que sigue (con OFFSET se repetía
    /// la que quedaba en el borde). La que subió la trae el refresco de la primera página, no la siguiente.</summary>
    [Fact]
    public async Task TheCursorPagesByPositionSoActivityBetweenPagesNeitherRepeatsNorSkips()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (one, _) = await IngestAsync(f, "CO.1", "w1", 1760000100, "Uno");
        var (two, _) = await IngestAsync(f, "CO.2", "w2", 1760000200, "Dos");
        var (three, _) = await IngestAsync(f, "CO.3", "w3", 1760000300, "Tres");
        var (four, _) = await IngestAsync(f, "CO.4", "w4", 1760000400, "Cuatro");

        var firstPage = await ListAsync(f, "?pageSize=2");
        Assert.Equal([four, three], Ids(firstPage));
        // «Uno» escribe: sube al tope.
        await IngestAsync(f, "CO.1", "w5", 1760000500, "Uno");
        var secondPage = await ListAsync(f, $"?pageSize=2&cursor={NextCursor(firstPage)}");

        Assert.Equal([two], Ids(secondPage));
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextCursor").ValueKind);
        Assert.Equal([one, four, three, two], Ids(await ListAsync(f, "")));
    }

    [Fact]
    public async Task ACursorTheServerDidNotIssueIsA422OnCursor()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await f.Client.GetAsync($"{ConversationsUrl(f.Tenant.TenantId)}?cursor=basura", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("cursor", (await ProblemAsync(response)).ErrorKeys);
    }

    private static string NextCursor(JsonElement page) =>
        Uri.EscapeDataString(page.GetProperty("nextCursor").GetString() ?? throw new InvalidOperationException("Se esperaba otra página."));

    [Fact]
    public async Task AnUnknownSortIsA422OnSort()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var response = await f.Client.GetAsync($"{ConversationsUrl(f.Tenant.TenantId)}?sort=random", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("sort", (await ProblemAsync(response)).ErrorKeys);
    }

    [Fact]
    public async Task EachRowCarriesTheTagsOfItsCustomerAndAnEmptyListWhenItHasNone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var (laura, lauraCustomer) = await IngestAsync(f, "CO.L", "w1", 1760000100, "Laura");
        var (mateo, _) = await IngestAsync(f, "CO.M", "w2", 1760000200, "Mateo");
        await TagAsync(f, "VIP", "purple", lauraCustomer);
        await TagAsync(f, "Antioquia", "green", lauraCustomer);

        var items = (await ListAsync(f, "")).GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetGuid());
        var detail = await f.Client.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, laura), Ct);

        var lauraTags = items[laura].GetProperty("customer").GetProperty("tags").EnumerateArray()
            .Select(tag => (tag.GetProperty("name").GetString(), tag.GetProperty("color").GetString())).ToArray();
        Assert.Equal([("Antioquia", "green"), ("VIP", "purple")], lauraTags);
        Assert.Equal(0, items[mateo].GetProperty("customer").GetProperty("tags").GetArrayLength());
        Assert.Equal(2, detail.GetProperty("customer").GetProperty("tags").GetArrayLength());
    }
}
