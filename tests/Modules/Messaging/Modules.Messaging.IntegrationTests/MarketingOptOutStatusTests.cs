using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-11 decisión 10 y §2: Meta acepta una plantilla de marketing a quien apagó las promociones y la reporta después, en el
/// <c>status</c> <c>failed</c> con el código 131050; ahí se prende la bandera del cliente. Cualquier otro código no la toca.</summary>
public sealed class MarketingOptOutStatusTests
{
    private sealed record Fixture(QepApiFactory Factory, string ConnectionString, RegisteredTenant Tenant, Guid ConnectionId, Guid ConversationId, Guid CustomerId, HttpClient Client);

    private static async Task<Fixture> ArrangeAsync(TestDatabase database, bool withCustomer = true)
    {
        var connectionString = database.GetConnectionString();
        var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        var customerId = await CreateCustomerAsync(factory, tenant, "Droguería Central", "300 123 4567");
        var conversationId = await SeedConversationAsync(factory, tenant.TenantId, connectionId, "573001234567");
        if (withCustomer)
        {
            await ExecuteAsync(connectionString, "UPDATE messaging.conversations SET customer_id = @c", ("c", customerId));
        }

        return new Fixture(factory, connectionString, tenant, connectionId, conversationId, customerId, factory.CreateClient());
    }

    private static async Task FailAsync(Fixture f, string wamid, int code)
    {
        await PostWebhookAsync(f.Client, MetaPayloads.Status("111", wamid, "failed", 1760000600, null, code, "Marketing"));
        await DrainDeliveriesAsync(f.Factory);
    }

    private static Task<string> OptOutAsync(Fixture f) =>
        ScalarAsync<string>(f.ConnectionString,
            "SELECT marketing_opt_out || '|' || coalesce(marketing_opt_out_source, '-') FROM customers.customers WHERE id = @id", ("id", f.CustomerId));

    [Fact]
    public async Task A131050FailedStatusMarksTheCustomersMarketingOptOutFromMeta()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.Tenant.TenantId, f.ConnectionId, "wamid.promo");

        await FailAsync(f, "wamid.promo", 131050);

        Assert.Equal("true|meta", await OptOutAsync(f));
        Assert.Equal("4|131050", await ScalarAsync<string>(f.ConnectionString, "SELECT status || '|' || failure_code FROM messaging.messages WHERE wamid = 'wamid.promo'"));
    }

    [Fact]
    public async Task AnotherFailureCodeLeavesTheCustomerAlone()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database);
        using var host = f.Factory;
        await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.Tenant.TenantId, f.ConnectionId, "wamid.promo");

        await FailAsync(f, "wamid.promo", 131026);

        Assert.Equal("false|-", await OptOutAsync(f));
    }

    [Fact]
    public async Task A131050ForAConversationWithoutCustomerMarksNobodyAndDoesNotBreakTheDelivery()
    {
        await using var database = await StartDatabaseAsync();
        var f = await ArrangeAsync(database, withCustomer: false);
        using var host = f.Factory;
        await SeedOutboundAsync(f.ConnectionString, f.ConversationId, f.Tenant.TenantId, f.ConnectionId, "wamid.promo");

        await FailAsync(f, "wamid.promo", 131050);

        Assert.Equal("false|-", await OptOutAsync(f));
        Assert.Equal("4", await ScalarAsync<string>(f.ConnectionString, "SELECT status::text FROM messaging.messages WHERE wamid = 'wamid.promo'"));
        Assert.Equal(1L, await CountAsync(f.ConnectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }
}
