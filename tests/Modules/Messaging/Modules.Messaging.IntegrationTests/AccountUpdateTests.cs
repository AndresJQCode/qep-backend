using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-10-09 §8.2 (account_update, D-M16, D-M12): la cuenta deshabilitada deja cada conexión
/// de la WABA en NeedsAttention con account_disabled; REINSTATE no reactiva.</summary>
public sealed class AccountUpdateTests
{
    [Theory]
    [InlineData("DISABLED_UPDATE", "DISABLE", "NeedsAttention|account_disabled")]
    [InlineData("ACCOUNT_DELETED", null, "NeedsAttention|account_disabled")]
    [InlineData("PARTNER_REMOVED", null, "NeedsAttention|account_disabled")]
    [InlineData("PARTNER_APP_UNINSTALLED", null, "NeedsAttention|account_disabled")]
    [InlineData("ACCOUNT_OFFBOARDED", null, "NeedsAttention|account_disabled")]
    [InlineData("DISABLED_UPDATE", "REINSTATE", "Active|-")]
    [InlineData("ACCOUNT_RECONNECTED", null, "Active|-")]
    [InlineData("ACCOUNT_VIOLATION", null, "Active|-")]
    public async Task TheAccountEventDecidesTheConnectionStatus(string @event, string? banState, string expected)
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var tenant = await RegisterTenantAsync(factory);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Ventas", "111", "222");
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Soporte", "112", "222");
        await SeedWhatsAppConnectionAsync(factory, tenant.TenantId, "Otra WABA", "113", "333");
        using var client = factory.CreateClient();

        await PostWebhookAsync(client, MetaPayloads.AccountUpdate("222", @event, banState));
        await DrainDeliveriesAsync(factory);

        Assert.Equal(expected, await ScalarAsync<string>(connectionString, "SELECT status || '|' || coalesce(last_failure_code, '-') FROM integrations.connections WHERE name = 'Ventas'"));
        Assert.Equal(expected, await ScalarAsync<string>(connectionString, "SELECT status || '|' || coalesce(last_failure_code, '-') FROM integrations.connections WHERE name = 'Soporte'"));
        Assert.Equal("Active|-", await ScalarAsync<string>(connectionString, "SELECT status || '|' || coalesce(last_failure_code, '-') FROM integrations.connections WHERE name = 'Otra WABA'"));
        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM messaging.webhook_deliveries WHERE processed_at IS NOT NULL"));
    }
}
