using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-10 §5.1 y §6.1.6: assigned=me|none|all, counts.mine y counts.unassigned sólo de abiertas
/// (D-A10), assignedTo con isMe calculado por el servidor (D-A13), el cliente por customer_id con isComplete (D-A8) y
/// el respaldo por teléfono para lo viejo, y la búsqueda por username y por nombre del cliente.</summary>
public sealed class ConversationListApiTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid Owner, Guid Beatriz, Guid BeatrizUser);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var (beatriz, beatrizUser) = await SeedMemberAsync(connectionString, tenant.TenantId, "Beatriz", "advisor");
        return new Fixture(factory, connectionString, tenant, connectionId, await OwnerMembershipIdAsync(connectionString, tenant), beatriz, beatrizUser);
    }

    private static async Task<Guid> IngestAsync(Fixture f, string userId, string wamid, long timestamp, string? username = null)
    {
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.Inbound("111", userId, null, wamid, timestamp, "hola", username: username));
        await DrainDeliveriesAsync(f.Factory);
        return await ScalarAsync<Guid>(f.ConnectionString, "SELECT id FROM messaging.conversations WHERE user_id = @u", ("u", userId));
    }

    private static Task<int> AssignAsync(Fixture f, Guid conversationId, Guid? memberId, string status = "Open") =>
        ExecuteAsync(f.ConnectionString,
            // @m::uuid: un DBNull sin tipo da 42P08 en el «@m IS NULL».
            "UPDATE messaging.conversations SET assigned_member_id = @m::uuid, assigned_at = CASE WHEN @m::uuid IS NULL THEN NULL ELSE now() END, status = @s WHERE id = @id",
            ("m", (object?)memberId ?? DBNull.Value), ("s", status), ("id", conversationId));

    private static async Task<JsonElement> ListAsync(HttpClient client, Guid tenantId, string query) =>
        await client.GetFromJsonAsync<JsonElement>($"{ConversationsUrl(tenantId)}{query}", TestContext.Current.CancellationToken);

    private static Guid[] Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();

    [Fact]
    public async Task TheAssignedFilterAndItsCountsOnlyCountOpenConversations()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var mine = await IngestAsync(f, "CO.A", "w1", 1760000100);
        var hers = await IngestAsync(f, "CO.B", "w2", 1760000200);
        var nobodys = await IngestAsync(f, "CO.C", "w3", 1760000300);
        var nobodysResolved = await IngestAsync(f, "CO.D", "w4", 1760000400);
        var mineResolved = await IngestAsync(f, "CO.E", "w5", 1760000500);
        await AssignAsync(f, mine, f.Owner);
        await AssignAsync(f, hers, f.Beatriz);
        await AssignAsync(f, nobodysResolved, null, "Resolved");
        await AssignAsync(f, mineResolved, f.Owner, "Resolved");
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var me = await ListAsync(client, f.Tenant.TenantId, "?assigned=me");
        var none = await ListAsync(client, f.Tenant.TenantId, "?assigned=none");
        var all = await ListAsync(client, f.Tenant.TenantId, "?assigned=all");
        var resolvedMine = await ListAsync(client, f.Tenant.TenantId, "?status=Resolved&assigned=me");

        Assert.Equal([mine], Ids(me));
        Assert.Equal([nobodys], Ids(none));
        Assert.Equal([nobodys, hers, mine], Ids(all));
        Assert.Equal([mineResolved], Ids(resolvedMine));
        var counts = all.GetProperty("counts");
        Assert.Equal((3, 1, 1), (counts.GetProperty("open").GetInt32(), counts.GetProperty("mine").GetInt32(), counts.GetProperty("unassigned").GetInt32()));
    }

    // D-A13: la SPA no conoce su memberId; isMe lo decide el servidor con la membresía de quien llama.
    [Fact]
    public async Task IsMeIsTrueOnlyForTheAssigneeAndAssignedToIsNullWhenUnassigned()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var hers = await IngestAsync(f, "CO.B", "w1", 1760000100);
        var nobodys = await IngestAsync(f, "CO.C", "w2", 1760000200);
        await AssignAsync(f, hers, f.Beatriz);
        using var owner = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);
        using var beatriz = CreateClient(f.Factory, f.BeatrizUser, f.Tenant.TenantId, ReadPermissions);

        var seenByOwner = await owner.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, hers), TestContext.Current.CancellationToken);
        var seenByHer = await beatriz.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, hers), TestContext.Current.CancellationToken);
        var herList = await ListAsync(beatriz, f.Tenant.TenantId, string.Empty);
        var unassigned = await owner.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, nobodys), TestContext.Current.CancellationToken);

        Assert.Equal((f.Beatriz, "Beatriz", false), AssignedTo(seenByOwner));
        Assert.Equal((f.Beatriz, "Beatriz", true), AssignedTo(seenByHer));
        Assert.True(herList.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == hers)
            .GetProperty("assignedTo").GetProperty("isMe").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unassigned.GetProperty("assignedTo").ValueKind);
    }

    // D-M20: la membresía que ya no está se ve como «Miembro eliminado», nunca null.
    [Fact]
    public async Task AnAssigneeThatNoLongerExistsIsMiembroEliminado()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var conversation = await IngestAsync(f, "CO.A", "w1", 1760000100);
        await AssignAsync(f, conversation, Guid.CreateVersion7());
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var body = await client.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, conversation), TestContext.Current.CancellationToken);

        Assert.Equal("Miembro eliminado", body.GetProperty("assignedTo").GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("assignedTo").GetProperty("isMe").GetBoolean());
    }

    [Theory]
    [InlineData("mine")]
    [InlineData("ME")]
    [InlineData("unassigned")]
    public async Task AnUnknownAssignedIsAValidationErrorOnThatKey(string value)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var response = await client.GetAsync($"{ConversationsUrl(f.Tenant.TenantId)}?assigned={value}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, errorKeys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(["assigned"], errorKeys);
    }

    // §6.1.6: la búsqueda encuentra por username y por el nombre del cliente vía customer_id, sin teléfono de por medio.
    [Fact]
    public async Task TheSearchFindsByUsernameAndByTheLinkedCustomersName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var laura = await IngestAsync(f, "CO.L", "w1", 1760000100, username: "laura.p");
        await IngestAsync(f, "CO.P", "w2", 1760000200);
        await ExecuteAsync(f.ConnectionString,
            "UPDATE customers.customers SET name = 'Droguería Central' WHERE id = (SELECT customer_id FROM messaging.conversations WHERE id = @id)", ("id", laura));
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        Assert.Equal([laura], Ids(await ListAsync(client, f.Tenant.TenantId, "?search=aura.p")));
        Assert.Equal([laura], Ids(await ListAsync(client, f.Tenant.TenantId, "?search=drogue")));
    }

    // D-A8 y §6.1.2: isComplete viaja; una conversación vieja sin customer_id se empareja por teléfono como antes.
    [Fact]
    public async Task TheCustomerCarriesIsCompleteAndALegacyConversationFallsBackToThePhone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        var complete = await CreateCustomerAsync(f.Factory, f.Tenant, name: "Droguería Central", phone: "300 123 4567");
        var legacy = await SeedConversationAsync(f.Factory, f.Tenant.TenantId, f.ConnectionId, "573001234567");
        var fresh = await IngestAsync(f, "CO.N", "w1", 1760000100);
        using var client = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId, ReadPermissions);

        var legacyBody = await client.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, legacy), TestContext.Current.CancellationToken);
        var freshBody = await client.GetFromJsonAsync<JsonElement>(ConversationUrl(f.Tenant.TenantId, fresh), TestContext.Current.CancellationToken);

        Assert.Equal((complete, true), (legacyBody.GetProperty("customer").GetProperty("id").GetGuid(), legacyBody.GetProperty("customer").GetProperty("isComplete").GetBoolean()));
        Assert.False(freshBody.GetProperty("customer").GetProperty("isComplete").GetBoolean());
    }

    private static (Guid, string?, bool) AssignedTo(JsonElement conversation)
    {
        var assigned = conversation.GetProperty("assignedTo");
        return (assigned.GetProperty("memberId").GetGuid(), assigned.GetProperty("displayName").GetString(), assigned.GetProperty("isMe").GetBoolean());
    }
}
