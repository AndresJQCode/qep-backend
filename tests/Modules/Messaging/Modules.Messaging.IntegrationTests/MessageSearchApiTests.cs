using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.8 y §12: sólo el tenant; acentos y flexiones; prefijo; una leyenda; before y
/// hasMore; conversationId; operadores → 200 vacío; q corto → 422 en q; before ajeno → 404; el timeout → 422 en q.</summary>
public sealed class MessageSearchApiTests
{
    private static readonly string[] OnQ = ["q"];
    private static readonly string[] OnTo = ["to"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, HttpClient Client, Guid ConversationA, Guid ConversationB);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, other.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        await SeedWhatsAppConnectionAsync(factory, other.TenantId, "Ajena", "999", "888");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", 1760000001, "Hola, soy de la Droguería Central"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w2", 1760000002, "Tienen pedidos pendientes?"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573009999999", "w3", 1760000003, "pedido urgente"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundMedia("111", "573009999999", "w4", 1760000004, "image", "m1", "image/jpeg", caption: "foto del pedido"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("999", "573001234567", "w5", 1760000005, "pedido de otro tenant"));
        await DrainDeliveriesAsync(factory);
        var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversations = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("contact").GetProperty("waId").GetString()!, item => item.GetProperty("id").GetGuid());
        return new Fixture(factory, connectionString, tenant, client, conversations["573001234567"], conversations["573009999999"]);
    }

    /// <summary>Muchos entrantes de texto en una sentencia, para que la búsqueda tenga qué ordenar.</summary>
    private static async Task BulkInboundAsync(Fixture f, Guid conversationId, string text, int count)
    {
        var connectionId = await ScalarAsync<Guid>(f.ConnectionString, "SELECT connection_id FROM messaging.conversations WHERE id = @id", ("id", conversationId));
        await ExecuteAsync(
            f.ConnectionString,
            """
            INSERT INTO messaging.messages (id, conversation_id, tenant_id, connection_id, occurred_at, direction, kind, status, text, created_at)
            SELECT gen_random_uuid(), @conversationId, @tenantId, @connectionId, to_timestamp(1750000000 + n), 1, 1, 2, @text, now()
            FROM generate_series(1, @count) AS n
            """,
            ("conversationId", conversationId), ("tenantId", f.Tenant.TenantId), ("connectionId", connectionId), ("text", text), ("count", count));
    }

    private static void AssertValidationOn((string? Code, string[] ErrorKeys) problem, string[] keys)
    {
        Assert.Equal("validation.failed", problem.Code);
        Assert.Equal(keys, problem.ErrorKeys);
    }

    private static Task<JsonElement> SearchAsync(Fixture f, string query) =>
        f.Client.GetFromJsonAsync<JsonElement>($"{SearchUrl(f.Tenant.TenantId)}?{query}", Ct);

    [Theory]
    [InlineData("q=drogueria", new[] { "w1" })]
    [InlineData("q=pedido", new[] { "w4", "w3", "w2" })]
    [InlineData("q=drog", new[] { "w1" })]
    [InlineData("q=pedido%20urgente", new[] { "w3" })]
    [InlineData("q=foto", new[] { "w4" })]
    public async Task ItFindsByWordWithoutAccentsNorInflectionsOnlyInTheTenant(string query, string[] expectedWamids)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var page = await SearchAsync(f, query);

        var ids = page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        var wamids = new List<string>();
        foreach (var id in ids)
        {
            wamids.Add(await ScalarAsync<string>(f.ConnectionString, "SELECT wamid FROM messaging.messages WHERE id = @id", ("id", id)));
        }

        Assert.Equal(expectedWamids, wamids);
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task EachHitCarriesItsConversationContactCustomerAndConnection()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var hit = (await SearchAsync(f, "q=drogueria")).GetProperty("items")[0];

        Assert.Equal(f.ConversationA, hit.GetProperty("conversationId").GetGuid());
        Assert.Equal("573001234567", hit.GetProperty("contact").GetProperty("waId").GetString());
        Assert.Equal("Ventas", hit.GetProperty("connectionName").GetString());
        Assert.Equal(JsonValueKind.Null, hit.GetProperty("customer").ValueKind);
        Assert.Equal("Text", hit.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ItPagesWithBeforeAndFiltersByConversationAndDates()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var first = await SearchAsync(f, "q=pedido&limit=2");
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var before = first.GetProperty("items")[1].GetProperty("id").GetGuid();
        var second = await SearchAsync(f, $"q=pedido&limit=2&before={before}");
        Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.False(second.GetProperty("hasMore").GetBoolean());

        var onlyB = await SearchAsync(f, $"q=pedido&conversationId={f.ConversationB}");
        Assert.Equal(2, onlyB.GetProperty("items").GetArrayLength());
        var dated = await SearchAsync(f, $"q=pedido&from={Uri.EscapeDataString(DateTimeOffset.FromUnixTimeSeconds(1760000003).ToString("O"))}&to={Uri.EscapeDataString(DateTimeOffset.FromUnixTimeSeconds(1760000003).ToString("O"))}");
        Assert.Single(dated.GetProperty("items").EnumerateArray());
    }

    [Theory]
    [InlineData("q=%26%7C%21%3A%2A%28%29%27")]
    [InlineData("q=de%20la")]
    public async Task OperatorsAndStopWordsAnswer200Empty(string query)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;

        var page = await SearchAsync(f, query);

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task AShortQAnInvertedRangeAndAForeignBeforeAreRejectedWithTheirCodes()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ReadPermissions);
        var mine = (await SearchAsync(f, "q=drogueria")).GetProperty("items")[0].GetProperty("id").GetGuid();

        var shortQ = await f.Client.GetAsync($"{SearchUrl(f.Tenant.TenantId)}?q=a", Ct);
        var inverted = await f.Client.GetAsync($"{SearchUrl(f.Tenant.TenantId)}?q=pedido&from=2026-10-09T00:00:00Z&to=2026-10-08T00:00:00Z", Ct);
        var foreignBefore = await otherClient.GetAsync($"{SearchUrl(other.TenantId)}?q=pedido&before={mine}", Ct);
        var foreignConversation = await otherClient.GetAsync($"{SearchUrl(other.TenantId)}?q=pedido&conversationId={f.ConversationA}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, shortQ.StatusCode);
        AssertValidationOn(await ProblemAsync(shortQ), OnQ);
        AssertValidationOn(await ProblemAsync(inverted), OnTo);
        Assert.Equal(HttpStatusCode.NotFound, foreignBefore.StatusCode);
        Assert.Equal("messaging.message.not_found", (await ProblemAsync(foreignBefore)).Code);
        Assert.Equal(HttpStatusCode.NotFound, foreignConversation.StatusCode);
        Assert.Equal("messaging.conversation.not_found", (await ProblemAsync(foreignConversation)).Code);
    }

    // §8.8, paso 4: un 57014 nunca es 500.
    [Fact]
    public async Task ATimeoutIsAValidationErrorOnQ()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var slow = f.Factory.WithWebHostBuilder(builder => builder.UseSetting("Messaging:Search:StatementTimeoutMs", "1"));
        using var client = CreateClient(slow, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        // pg_sleep dentro de la misma transacción no se puede inyectar; se fuerza el timeout con 1 ms y un
        // término con 20 000 coincidencias que hay que ordenar. Con los cinco mensajes del arreglo la consulta
        // cabía en 1 ms y la prueba no demostraba nada.
        await BulkInboundAsync(f, f.ConversationB, "otro pedido de la semana", 20000);

        var response = await client.GetAsync($"{SearchUrl(f.Tenant.TenantId)}?q=pedido", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        AssertValidationOn(await ProblemAsync(response), OnQ);
        using var document = JsonDocument.Parse(body);
        Assert.Equal(
            "Busca con una palabra más específica o acota las fechas",
            document.RootElement.GetProperty("errors").GetProperty("q")[0].GetString());
    }
}
