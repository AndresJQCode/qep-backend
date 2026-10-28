using System.Text.Json.Nodes;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Decisión del owner 2026-10-12 (P1): la persona que toca el botón «Detener promociones» de una plantilla apaga el marketing de su
/// ficha, con origen <c>button</c>. Sólo el botón de respuesta rápida: el mismo texto escrito a mano no cuenta.</summary>
public sealed class MarketingOptOutButtonTests
{
    private const string UserId = "CO.573001234567";

    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid CustomerId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var customerId = await CreateCustomerAsync(factory, tenant, "Droguería Central", "300 123 4567");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET customer_id = @c, user_id = @u WHERE id = @id", ("c", customerId), ("u", UserId), ("id", conversationId));
        return new Fixture(factory, connectionString, tenant, customerId, factory.CreateClient());
    }

    private static string ButtonReply(string wamid, string text, string payload)
    {
        var message = new JsonObject
        {
            ["from_user_id"] = UserId,
            ["from"] = "573001234567",
            ["id"] = wamid,
            ["timestamp"] = "1760000000",
            ["type"] = "button",
            ["button"] = new JsonObject { ["payload"] = payload, ["text"] = text },
        };
        var value = new JsonObject
        {
            ["messaging_product"] = "whatsapp",
            ["metadata"] = new JsonObject { ["display_phone_number"] = "15550000000", ["phone_number_id"] = "111" },
            ["contacts"] = new JsonArray(new JsonObject { ["profile"] = new JsonObject { ["name"] = "Laura" }, ["user_id"] = UserId, ["wa_id"] = "573001234567" }),
            ["messages"] = new JsonArray(message),
        };
        return MetaPayloads.Change("messages", value.ToJsonString());
    }

    private static async Task IngestAsync(Fixture f, string json)
    {
        await PostWebhookAsync(f.Client, json);
        await DrainDeliveriesAsync(f.Factory);
    }

    private static Task<string> OptOutAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT marketing_opt_out || '|' || coalesce(marketing_opt_out_source, '-') FROM customers.customers WHERE id = @id", ("id", f.CustomerId));

    [Fact]
    public async Task TheConfiguredQuickReplyButtonMarksTheOptOutWithTheButtonSource()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await IngestAsync(f, ButtonReply("wamid.b1", "Detener promociones", "payload-1"));

        Assert.Equal("true|button", await OptOutAsync(f));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.messages WHERE wamid = 'wamid.b1'"));
    }

    [Fact]
    public async Task ReceivingTheButtonTwiceKeepsTheOriginalRecord()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await IngestAsync(f, ButtonReply("wamid.b1", "detener PROMOCIONES", "p"));
        var first = await ScalarAsync<string>(f.ConnectionString, "SELECT marketing_opt_out_at::text FROM customers.customers WHERE id = @id", ("id", f.CustomerId));
        await IngestAsync(f, ButtonReply("wamid.b2", "Detener promociones", "p"));

        Assert.Equal("true|button", await OptOutAsync(f));
        Assert.Equal(first, await ScalarAsync<string>(f.ConnectionString, "SELECT marketing_opt_out_at::text FROM customers.customers WHERE id = @id", ("id", f.CustomerId)));
    }

    [Fact]
    public async Task APlainTextWithTheSameWordsDoesNotMarkAnything()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await IngestAsync(f, MetaPayloads.Inbound("111", UserId, "573001234567", "wamid.t1", 1760000000, "Detener promociones"));

        Assert.Equal("false|-", await OptOutAsync(f));
    }

    [Fact]
    public async Task AnotherQuickReplyButtonLeavesTheCustomerAlone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;

        await IngestAsync(f, ButtonReply("wamid.b3", "Confirmar", "ok"));

        Assert.Equal("false|-", await OptOutAsync(f));
    }
}
