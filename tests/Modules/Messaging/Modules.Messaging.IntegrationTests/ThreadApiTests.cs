using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §5.3 y §8.7: hilo cronológico, paginación hacia atrás con before y hasMore, la
/// forma de cada kind, failureReason y sentBy; Review Focus 3: un before ajeno es validation.failed.</summary>
public sealed class ThreadApiTests
{
    private static readonly string[] BeforeKey = ["before"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheThreadIsChronologicalAndPagesBackwardsWithBefore()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        for (var i = 1; i <= 5; i++)
        {
            await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", $"w{i}", 1760000000 + i, $"m{i}"));
        }

        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var newest = await client.GetFromJsonAsync<JsonElement>($"{MessagesUrl(tenant.TenantId, conversationId)}?limit=2", Ct);
        var items = newest.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(["m4", "m5"], items.Select(item => item.GetProperty("text").GetString()));
        Assert.True(newest.GetProperty("hasMore").GetBoolean());
        Assert.Equal("Inbound", items[0].GetProperty("direction").GetString());
        Assert.Equal("Delivered", items[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("failureReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("sentBy").ValueKind);

        var before = items[0].GetProperty("id").GetGuid();
        var older = await client.GetFromJsonAsync<JsonElement>($"{MessagesUrl(tenant.TenantId, conversationId)}?limit=2&before={before}", Ct);
        Assert.Equal(["m2", "m3"], older.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("text").GetString()));
        Assert.True(older.GetProperty("hasMore").GetBoolean());
        var oldest = await client.GetFromJsonAsync<JsonElement>($"{MessagesUrl(tenant.TenantId, conversationId)}?limit=5&before={older.GetProperty("items")[0].GetProperty("id").GetGuid()}", Ct);
        Assert.Equal(["m1"], oldest.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("text").GetString()));
        Assert.False(oldest.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task MediaLocationAndFailedMessagesHaveTheirShape()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundMedia("111", "573001234567", "w1", 1760000001, "document", "media-1", "application/pdf", caption: "la orden", filename: "orden.pdf"));
        await PostWebhookAsync(anonymous, MetaPayloads.Change("messages", MetaPayloads.LocationValue("111", "573001234567", "w2", 1760000002, 4.6, -74.1, "Casa", "Calle 1")));
        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var memberId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM tenancy.memberships WHERE tenant_id = @t", ("t", tenant.TenantId));
        var failedId = await SeedOutboundAsync(connectionString, conversationId, tenant.TenantId, connectionId, "wamid.f", status: 4, failureCode: 131047, occurredAtUnix: 1760000003);
        await ExecuteAsync(connectionString, "UPDATE messaging.messages SET sent_by_member_id = @m WHERE id = @id", ("m", memberId), ("id", failedId));

        var page = await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), Ct);
        var items = page.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(["Document", "Location", "Text"], items.Select(item => item.GetProperty("kind").GetString()));
        var media = items[0].GetProperty("media");
        Assert.Equal(MediaUrl(tenant.TenantId, items[0].GetProperty("id").GetGuid()), media.GetProperty("url").GetString());
        Assert.Equal("application/pdf", media.GetProperty("mimeType").GetString());
        Assert.Equal("orden.pdf", media.GetProperty("fileName").GetString());
        Assert.Equal("la orden", media.GetProperty("caption").GetString());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("text").ValueKind);
        Assert.Equal(4.6, items[1].GetProperty("location").GetProperty("latitude").GetDouble());
        Assert.Equal("Casa", items[1].GetProperty("location").GetProperty("name").GetString());
        Assert.Equal("Outbound", items[2].GetProperty("direction").GetString());
        Assert.Equal("Failed", items[2].GetProperty("status").GetString());
        Assert.Equal("Pasaron más de 24 horas desde el último mensaje de la persona: WhatsApp sólo acepta plantillas aprobadas.", items[2].GetProperty("failureReason").GetString());
        Assert.Equal(memberId, items[2].GetProperty("sentBy").GetProperty("memberId").GetGuid());
        Assert.False(string.IsNullOrEmpty(items[2].GetProperty("sentBy").GetProperty("displayName").GetString()));
        Assert.DoesNotContain("failureTitle", page.GetRawText(), StringComparison.Ordinal);
    }

    // D-M20: displayName nunca es null.
    [Fact]
    public async Task AMessageFromAMemberThatNoLongerExistsSaysMiembroEliminado()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        var ghost = Guid.CreateVersion7();
        var messageId = await SeedOutboundAsync(connectionString, conversationId, tenant.TenantId, connectionId, "wamid.g");
        await ExecuteAsync(connectionString, "UPDATE messaging.messages SET sent_by_member_id = @m WHERE id = @id", ("m", ghost), ("id", messageId));
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);

        var sentBy = (await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversationId), Ct)).GetProperty("items")[0].GetProperty("sentBy");

        Assert.Equal(ghost, sentBy.GetProperty("memberId").GetGuid());
        Assert.Equal("Miembro eliminado", sentBy.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task ABeforeFromAnotherTenantIsAValidationError()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        var other = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await EnableMessagingAsync(connectionString, other.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        await SeedWhatsAppConnectionAsync(factory, other.TenantId, "Otra", "333", "444");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", 1760000001, "a"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("333", "573001234567", "w2", 1760000002, "b"));
        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        using var otherClient = CreateClient(factory, other.OwnerUserId, other.TenantId, ReadPermissions);
        var conversationId = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var otherConversationId = (await otherClient.GetFromJsonAsync<JsonElement>(ConversationsUrl(other.TenantId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var foreignMessage = (await otherClient.GetFromJsonAsync<JsonElement>(MessagesUrl(other.TenantId, otherConversationId), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var response = await client.GetAsync($"{MessagesUrl(tenant.TenantId, conversationId)}?before={foreignMessage}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(BeforeKey, keys);
    }

    // Review Focus 3.
    [Fact]
    public async Task ABeforeFromAnotherConversationIsAValidationError()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        using var anonymous = factory.CreateClient();
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573001234567", "w1", 1760000001, "a"));
        await PostWebhookAsync(anonymous, MetaPayloads.InboundText("111", "573009999999", "w2", 1760000002, "b"));
        await DrainDeliveriesAsync(factory);
        using var client = CreateClient(factory, tenant.OwnerUserId, tenant.TenantId, ReadPermissions);
        var conversations = (await client.GetFromJsonAsync<JsonElement>(ConversationsUrl(tenant.TenantId), Ct)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();
        var foreignMessage = (await client.GetFromJsonAsync<JsonElement>(MessagesUrl(tenant.TenantId, conversations[0]), Ct)).GetProperty("items")[0].GetProperty("id").GetGuid();

        var response = await client.GetAsync($"{MessagesUrl(tenant.TenantId, conversations[1])}?before={foreignMessage}", Ct);
        var unknown = await client.GetAsync($"{MessagesUrl(tenant.TenantId, conversations[1])}?before={Guid.CreateVersion7()}", Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var (code, keys) = await ProblemAsync(response);
        Assert.Equal("validation.failed", code);
        Assert.Equal(BeforeKey, keys);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }
}
