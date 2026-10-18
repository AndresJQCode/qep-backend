using System.Net;
using System.Net.Http.Json;
using System.Text;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>Spec 2026-10-15 §5.1 y §12 contra el host: firma antes de tocar la base (401 sin fila), una fila por
/// <c>X-Shopify-Webhook-Id</c> aunque Shopify reintente, tope del cuerpo (413) y exención de CSRF por el prefijo
/// <c>/api/webhooks/</c>, que sólo se ve con el stub de auth apagado (CLAUDE.md).</summary>
public sealed class ShopifyWebhookApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<long> CountDeliveriesAsync(string connectionString) =>
        ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.webhook_deliveries");

    [Fact]
    public async Task WebhookSignedReturns200AndRow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();
        var webhookId = Guid.CreateVersion7().ToString();

        var response = await PostWebhookAsync(client, "orders/create", webhookId, OrderFixture("taxes-included"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1L, await CountDeliveriesAsync(connectionString));
        Assert.Equal(
            $"orders/create|{Shop}|2026-10|0|pending|#1001",
            await ScalarAsync<string>(
                connectionString,
                """
                SELECT topic || '|' || shop_domain || '|' || api_version || '|' || attempts || '|'
                       || CASE WHEN processed_at IS NULL THEN 'pending' ELSE 'processed' END || '|' || (payload->>'name')
                FROM shopify.webhook_deliveries WHERE webhook_id = @webhookId
                """,
                ("webhookId", webhookId)));
    }

    [Fact]
    public async Task WebhookUnsignedReturns401NoRow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();
        var body = OrderFixture("taxes-included");

        var unsigned = await PostWebhookAsync(client, "orders/create", Guid.CreateVersion7().ToString(), body, signature: null);
        var otherSecret = await PostWebhookAsync(
            client, "orders/create", Guid.CreateVersion7().ToString(), body, signature: Sign(body, "another-app-secret"));
        var tampered = await PostWebhookAsync(
            client, "orders/create", Guid.CreateVersion7().ToString(), Encoding.UTF8.GetBytes("{\"id\":1}"), signature: Sign(body));

        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);
        Assert.Contains("shopify.webhook.signature_invalid", await unsigned.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(0L, await CountDeliveriesAsync(connectionString));
    }

    [Fact]
    public async Task WebhookDuplicateOneRow()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();
        var webhookId = Guid.CreateVersion7().ToString();

        var first = await PostWebhookAsync(client, "orders/create", webhookId, OrderFixture("taxes-included"));
        var retry = await PostWebhookAsync(client, "orders/create", webhookId, OrderFixture("taxes-included"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1L, await CountDeliveriesAsync(connectionString));
    }

    [Fact]
    public async Task WebhookTooLargeReturns413()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var client = factory.CreateClient();
        // 1 MiB + 1 byte de JSON válido, firmado: el tope va antes que la firma.
        var body = Encoding.UTF8.GetBytes("{\"note\":\"" + new string('a', 1_048_577 - 11) + "\"}");
        Assert.Equal(1_048_577, body.Length);

        var response = await PostWebhookAsync(client, "orders/create", Guid.CreateVersion7().ToString(), body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, await CountDeliveriesAsync(connectionString));
    }

    [Fact]
    public async Task WebhookIsExemptFromCsrf()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        using var realAuth = factory.WithRealAuthentication();
        using var client = realAuth.CreateClient();

        var webhook = await PostWebhookAsync(client, "orders/create", Guid.CreateVersion7().ToString(), OrderFixture("taxes-included"));
        // El contraste: con el stub apagado el middleware existe, y una ruta que no es webhook sin X-Qep-Client da 403.
        var tenantRoute = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{Guid.CreateVersion7()}/integrations/shopify/link", new { installId = Guid.CreateVersion7() }, Ct);

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.Equal(1L, await CountDeliveriesAsync(connectionString));
        Assert.Equal(HttpStatusCode.Forbidden, tenantRoute.StatusCode);
        Assert.Contains("X-Qep-Client", await tenantRoute.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }
}
