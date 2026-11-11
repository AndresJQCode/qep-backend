using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Un destinatario de campaña puede quedar sin cliente (P10): al borrar el cliente se anula <c>customer_id</c> para conservar el
/// histórico de la campaña. El despachador lo trata como inalcanzable y nunca le abre una conversación.</summary>
public sealed class CampaignRecipientWithoutCustomerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Catalog = """
        {"data":[
          {"id":"1","name":"promo","language":"es","status":"APPROVED","category":"MARKETING",
           "components":[{"type":"BODY","text":"Hola {{1}}"}]}
        ]}
        """;

    private static async Task<(QepApiFactory Pod, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host, string ConnectionString, RegisteredTenant Tenant, Guid CampaignId)>
        ArrangeAsync(TestDatabase database)
    {
        var connectionString = database.GetConnectionString();
        var pod = new QepApiFactory(connectionString);
        var host = pod.WithWebHostBuilder(_ => { });
        var tenant = await RegisterTenantAsync(host);
        await EnableMessagingAsync(connectionString, tenant.TenantId);
        var connectionId = await SeedWhatsAppConnectionAsync(host, tenant.TenantId, "Ventas", "111", "222");
        pod.MetaHandler.Respond("/222/message_templates", HttpStatusCode.OK, Catalog);
        var client = CreateClient(host, tenant.OwnerUserId, tenant.TenantId, CampaignPermissions);
        var url = $"/api/v1/tenants/{tenant.TenantId}/messaging/campaigns";
        var created = await SendAsync(client, HttpMethod.Post, url, new
        {
            name = "Promo",
            connectionId,
            template = new { name = "promo", language = "es" },
            parameters = new Dictionary<string, object> { ["1"] = new { source = "customerName" } },
            audience = new { tagIds = Array.Empty<Guid>(), customerIds = new[] { Guid.CreateVersion7() } },
            scheduledAt = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var launched = await SendAsync(client, HttpMethod.Post, $"{url}/{id}/launch", new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, launched.StatusCode);
        // La audiencia ya está congelada: el worker no debe volver a congelarla y pisar las filas sembradas.
        await ExecuteAsync(connectionString, "UPDATE messaging.campaigns SET audience_frozen_at = now() WHERE id = @id", ("id", id));
        return (pod, host, connectionString, tenant, id);
    }

    private static Task<int> SeedWithoutCustomerAsync(string connectionString, Guid tenantId, Guid campaignId, string name, string waId) =>
        ExecuteAsync(connectionString,
            """
            INSERT INTO messaging.campaign_recipients (id, campaign_id, tenant_id, customer_id, customer_name, wa_id, status, attempts, next_attempt_at)
            VALUES (@id, @campaign, @tenant, NULL, @name, @wa, 'Pending', 0, now() - interval '1 hour')
            """,
            ("id", Guid.CreateVersion7()), ("campaign", campaignId), ("tenant", tenantId), ("name", name), ("wa", waId));

    [Fact]
    public async Task TwoRecipientsWithoutCustomerFitInOneCampaign()
    {
        await using var database = await StartDatabaseAsync();
        var (pod, _, connectionString, tenant, campaignId) = await ArrangeAsync(database);
        using var _ = pod;

        await SeedWithoutCustomerAsync(connectionString, tenant.TenantId, campaignId, "Ana", "573009990001");
        await SeedWithoutCustomerAsync(connectionString, tenant.TenantId, campaignId, "Beto", "573009990002");

        Assert.Equal(2, await ScalarAsync<int>(connectionString,
            "SELECT count(*)::int FROM messaging.campaign_recipients WHERE campaign_id = @id AND customer_id IS NULL", ("id", campaignId)));
    }

    [Fact]
    public async Task AClaimedRecipientWithoutCustomerIsSkippedAsUnreachable()
    {
        await using var database = await StartDatabaseAsync();
        var (pod, host, connectionString, tenant, campaignId) = await ArrangeAsync(database);
        using var _ = pod;
        await SeedWithoutCustomerAsync(connectionString, tenant.TenantId, campaignId, "Ana", "573009990001");

        await DrainCampaignsAsync(host);

        Assert.Equal("Skipped", await ScalarAsync<string>(connectionString, "SELECT status FROM messaging.campaign_recipients WHERE customer_name = 'Ana'"));
        Assert.Equal("unreachable", await ScalarAsync<string>(connectionString, "SELECT failure_code FROM messaging.campaign_recipients WHERE customer_name = 'Ana'"));
        Assert.Equal(0, await ScalarAsync<int>(connectionString, "SELECT count(*)::int FROM messaging.conversations"));
    }
}
