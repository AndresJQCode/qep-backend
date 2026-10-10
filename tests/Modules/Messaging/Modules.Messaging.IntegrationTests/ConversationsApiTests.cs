using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §5.3 y §8.7: la lista con orden, counts, cliente emparejado por teléfono,
/// connectionName (y «Conexión eliminada»), búsqueda por perfil, número y cliente; el detalle; módulo
/// apagado, sin permiso y otro tenant → 403 con su código.</summary>
public sealed class ConversationsApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, string[]? permissions = null)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        return new Fixture(factory, connectionString, tenant, connectionId, CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, permissions ?? ManagePermissions));
    }

    private static async Task IngestAsync(Fixture f, string waId, string wamid, long timestamp, string text, string profileName = "Laura")
    {
        using var anonymous = f.Factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", waId, wamid, timestamp, text, profileName));
        await DrainDeliveriesAsync(f.Factory);
    }

    [Fact]
    public async Task TheListIsOrderedByActivityWithCountsCustomerAndConnectionName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "primero");
        await IngestAsync(f, "573009999999", "w2", 1760000200, "segundo", "Pedro");
        await IngestAsync(f, "573001234567", "w3", 1760000300, "tercero");
        // Un cliente de QEP con ese teléfono (Customers calcula phone_e164 = +573001234567).
        var customerId = await CreateCustomerAsync(f.Factory, f.Tenant, name: "Droguería Central", phone: "300 123 4567");

        var page = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(30, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, page.GetProperty("counts").GetProperty("open").GetInt32());
        Assert.Equal(3, page.GetProperty("counts").GetProperty("unread").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["573001234567", "573009999999"], items.Select(item => item.GetProperty("contact").GetProperty("waId").GetString()));
        var first = items[0];
        Assert.Equal("Laura", first.GetProperty("contact").GetProperty("profileName").GetString());
        Assert.Equal("Ventas", first.GetProperty("connectionName").GetString());
        Assert.Equal(f.ConnectionId, first.GetProperty("connectionId").GetGuid());
        Assert.Equal(customerId, first.GetProperty("customer").GetProperty("id").GetGuid());
        Assert.Equal("Droguería Central", first.GetProperty("customer").GetProperty("name").GetString());
        Assert.Equal("Open", first.GetProperty("status").GetString());
        Assert.Equal(2, first.GetProperty("unreadCount").GetInt32());
        Assert.Equal("Inbound", first.GetProperty("lastMessage").GetProperty("direction").GetString());
        Assert.Equal("Text", first.GetProperty("lastMessage").GetProperty("kind").GetString());
        Assert.Equal("tercero", first.GetProperty("lastMessage").GetProperty("preview").GetString());
        Assert.Equal("Delivered", first.GetProperty("lastMessage").GetProperty("status").GetString());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000300).AddHours(24), first.GetProperty("customerWindowExpiresAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("customer").ValueKind);
        Assert.True(first.GetProperty("version").GetInt64() >= 1);
    }

    [Theory]
    [InlineData("lau", "573001234567")]
    [InlineData("9999", "573009999999")]
    [InlineData("drogue", "573001234567")]
    [InlineData("nadie", null)]
    [InlineData("300 123", "573001234567")]
    // Revisión de la Task 14: el 9 suelto no busca por número (wa_id LIKE '%9%' traería a Pedro).
    [InlineData("Laura 9", null)]
    public async Task TheSearchMatchesProfileNumberAndCustomerName(string search, string? expectedWaId)
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "a");
        await IngestAsync(f, "573009999999", "w2", 1760000200, "b", "Pedro");
        await CreateCustomerAsync(f.Factory, f.Tenant, name: "Droguería Central", phone: "300 123 4567");

        var page = await f.Client.GetFromJsonAsync<JsonElement>($"{ConversationsUrl(f.Tenant.TenantId)}?search={Uri.EscapeDataString(search)}", Ct);

        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(expectedWaId is null ? [] : [expectedWaId], items.Select(item => item.GetProperty("contact").GetProperty("waId").GetString()));
        // total sí respeta la búsqueda (el scroll infinito para en page * pageSize >= total); counts no (§5.4).
        Assert.Equal(expectedWaId is null ? 0 : 1, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("counts").GetProperty("open").GetInt32());
        Assert.Equal(2, page.GetProperty("counts").GetProperty("unread").GetInt32());
    }

    [Fact]
    public async Task WithoutMessagesLastMessageIsNullAndAMediaWithoutCaptionHasNoPreview()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await SeedConversationAsync(f.Factory, f.Tenant.TenantId, f.ConnectionId, "573005555555");
        using (var anonymous = f.Factory.CreateClient())
        {
            await PostWebhookAsync(anonymous, MetaPayloads.InboundMedia("111", "573001234567", "w1", 1760000100, "image", "media-1", "image/jpeg"));
            await DrainDeliveriesAsync(f.Factory);
        }

        var items = (await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct)).GetProperty("items").EnumerateArray().ToArray();

        var empty = items.Single(item => item.GetProperty("contact").GetProperty("waId").GetString() == "573005555555");
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("lastMessage").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("customerWindowExpiresAt").ValueKind);
        var image = items.Single(item => item.GetProperty("contact").GetProperty("waId").GetString() == "573001234567").GetProperty("lastMessage");
        Assert.Equal("Image", image.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, image.GetProperty("preview").ValueKind);
    }

    [Fact]
    public async Task ADeletedConnectionShowsAPlaceholderName()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "a");
        await ExecuteAsync(f.ConnectionString, "DELETE FROM integrations.connections");

        var page = await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct);

        Assert.Equal("Conexión eliminada", page.GetProperty("items")[0].GetProperty("connectionName").GetString());
    }

    [Fact]
    public async Task TheDetailAnswersTheSameShapeAnd404InsideTheTenant()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "a");
        var id = (await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var detail = await f.Client.GetAsync(ConversationUrl(f.Tenant.TenantId, id), Ct);
        var missing = await f.Client.GetAsync(ConversationUrl(f.Tenant.TenantId, Guid.CreateVersion7()), Ct);

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal(id, (await detail.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("messaging.conversation.not_found", (await ProblemAsync(missing)).Code);
    }

    [Fact]
    public async Task ModuleOffMissingPermissionAndAnotherTenantAre403WithTheirCode()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var _ = f.Factory;
        await IngestAsync(f, "573001234567", "w1", 1760000100, "a");
        var id = (await f.Client.GetFromJsonAsync<JsonElement>(ConversationsUrl(f.Tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var other = await RegisterTenantAsync(f.Factory);
        await EnableMessagingAsync(f.ConnectionString, other.TenantId);
        using var otherClient = CreateClient(f.Factory, other.OwnerUserId, other.TenantId, ManagePermissions);
        using var noPermission = CreateClient(f.Factory, f.Tenant.OwnerUserId, f.Tenant.TenantId);

        var cross = await otherClient.GetAsync(ConversationUrl(f.Tenant.TenantId, id), Ct);
        var ownRouteForeignId = await otherClient.GetAsync(ConversationUrl(other.TenantId, id), Ct);
        var denied = await noPermission.GetAsync(ConversationsUrl(f.Tenant.TenantId), Ct);
        await ExecuteAsync(f.ConnectionString, "UPDATE tenancy.tenant_modules SET status = 'inactive' WHERE tenant_id = @t AND module_key = 'messaging'", ("t", f.Tenant.TenantId));
        var off = await f.Client.GetAsync(ConversationsUrl(f.Tenant.TenantId), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        Assert.Equal("authorization.denied", (await ProblemAsync(cross)).Code);
        Assert.Equal(HttpStatusCode.NotFound, ownRouteForeignId.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, off.StatusCode);
        Assert.Equal("tenancy.module_not_enabled", (await ProblemAsync(off)).Code);
    }
}
