using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Workers;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-15 §12 y CLAUDE.md (secretos): el token de la tienda y el client secret de la app no salen nunca del backend
/// salvo hacia Shopify. Se ejercen todas las rutas de <c>/shopify/*</c> (y la lista de conexiones de Integrations, que es la que la
/// pantalla de tiendas pide al lado) con un token sentinela, el webhook y su worker, la conciliación y el reintento; después se
/// busca el sentinela en cada respuesta, en todo lo que el host logueó y en <c>audit.entries</c>.
/// </summary>
public sealed class ShopifySecretLeakTests
{
    private const string Sentinel = "shpat_sentinel_token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TokenSentinelAbsentFromResponsesLogsAndAudit()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var logs = new RecordingLoggerProvider();
        var shopify = new FakeShopifyAdmin();
        using var factory = shopify.Factory(connectionString, services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs));
        var world = await ShopifyWorld.ArrangeAsync(factory, connectionString);
        var sync = GraphQlFixture("taxes-included");
        sync["id"] = "gid://shopify/Order/5550000000601";
        sync["name"] = "#1601";
        shopify.WithOrder(Shop, sync);
        using var client = CreateClient(
            factory,
            world.Tenant.OwnerUserId,
            world.TenantId,
            ShopifyPermissions.ConnectionRead,
            ShopifyPermissions.ConnectionManage,
            ShopifyPermissions.OrderRead,
            ShopifyPermissions.OrderManage);
        using var webhooks = factory.CreateClient();
        var bodies = new List<(string Call, string Body)>();
        async Task<string> CallAsync(string call, Func<Task<HttpResponseMessage>> send)
        {
            using var response = await send();
            var body = await response.Content.ReadAsStringAsync(Ct);
            bodies.Add(($"{call} → {(int)response.StatusCode}", body + string.Join('\n', response.Headers.Select(header => $"{header.Key}: {string.Join(',', header.Value)}"))));
            return body;
        }

        var tenantRoute = $"/api/v1/tenants/{world.TenantId}";
        var storeRoute = $"{tenantRoute}/shopify/stores/{world.ConnectionId}";

        // La tienda: lista, lectura (nace vacía), configuración con su versión.
        await CallAsync("GET stores", () => client.GetAsync($"{tenantRoute}/shopify/stores", Ct));
        await CallAsync("GET store", () => client.GetAsync(storeRoute, Ct));
        await CallAsync("PUT store", () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, storeRoute)
            {
                Content = JsonContent.Create(new JsonObject
                {
                    ["advisorMembershipId"] = world.AdvisorMembershipId,
                    ["billingCompanyId"] = world.Company.Id,
                    ["billingBankName"] = world.Company.BankName,
                    ["billingAccountNumber"] = world.Company.AccountNumber,
                    ["billingCurrency"] = "COP",
                    ["defaultClassificationId"] = world.ClassificationId,
                    ["documentAttributeKey"] = "Documento",
                    ["documentTypeAttributeKey"] = "tipo_documento",
                    ["defaultIdentificationType"] = "CC",
                }),
            };
            request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
            return client.SendAsync(request, Ct);
        });

        // Un pedido que entra por el webhook y otro que espera un producto.
        await CallAsync("POST webhook", () => PostWebhookAsync(webhooks, "orders/create", "wh-1", OrderFixture("taxes-included")));
        var waiting = (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;
        waiting["id"] = 5550000000602;
        waiting["name"] = "#1602";
        waiting["line_items"]![0]!["sku"] = "NO-EXISTE";
        await CallAsync("POST webhook waiting", () => PostWebhookAsync(webhooks, "orders/create", "wh-2", Encoding.UTF8.GetBytes(waiting.ToJsonString())));
        await ActivatorUtilities.CreateInstance<ShopifyWebhookDeliveryWorker>(factory.Services).DrainAsync(Ct);

        // «Sincronizar ahora» y la conciliación, que llaman a Shopify con el token.
        await CallAsync("POST sync", () => client.PostAsync($"{storeRoute}/sync", content: null, Ct));
        await ActivatorUtilities.CreateInstance<ShopifyReconcileWorker>(factory.Services).RunOnceAsync(Ct);

        // La bandeja: lista, resumen, reintento y revisión.
        var imported = await ScalarAsync<Guid>(connectionString, "SELECT id FROM shopify.orders WHERE shopify_order_id = 5550000000001");
        var waitingId = await ScalarAsync<Guid>(connectionString, "SELECT id FROM shopify.orders WHERE shopify_order_id = 5550000000602");
        await CallAsync("GET orders", () => client.GetAsync($"{tenantRoute}/shopify/orders?state=Waiting&state=Imported&state=Ignored", Ct));
        await CallAsync("GET summary", () => client.GetAsync($"{tenantRoute}/shopify/orders/summary", Ct));
        await CallAsync("POST retry", () => client.PostAsync($"{tenantRoute}/shopify/orders/{waitingId}/retry", content: null, Ct));
        await CallAsync("POST review", () => client.PostAsync($"{tenantRoute}/shopify/orders/{imported}/warnings/review", content: null, Ct));

        // Lo que la pantalla de tiendas lee de Integrations, y los errores (otro tenant, id que no existe).
        await CallAsync("GET connections", () => client.GetAsync($"{tenantRoute}/integrations/connections", Ct));
        await CallAsync("GET connection", () => client.GetAsync($"{tenantRoute}/integrations/connections/{world.ConnectionId}", Ct));
        await CallAsync("GET other tenant", () => client.GetAsync($"/api/v1/tenants/{Guid.CreateVersion7()}/shopify/stores/{world.ConnectionId}", Ct));
        await CallAsync("GET unknown store", () => client.GetAsync($"{tenantRoute}/shopify/stores/{Guid.CreateVersion7()}", Ct));

        // Que la prueba haya ejercido el camino del token: Shopify lo recibió y la sincronización importó.
        Assert.Contains(shopify.Requests, request => request.Token == Sentinel);
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM shopify.orders WHERE shopify_order_id = 5550000000601"));
        // Cada llamada llegó a su handler: los éxitos y los dos rechazos esperados, no un 500 ni un 401 que no diga nada.
        Assert.Equal(
            [
                "GET stores → 200", "GET store → 200", "PUT store → 200", "POST webhook → 200", "POST webhook waiting → 200",
                "POST sync → 200", "GET orders → 200", "GET summary → 200", "POST retry → 200", "POST review → 200",
                "GET connections → 200", "GET connection → 200", "GET other tenant → 403", "GET unknown store → 403",
            ],
            bodies.Select(entry => entry.Call));

        var audit = await ScalarAsync<string>(connectionString, "SELECT coalesce(string_agg(e::text, E'\\n'), '') FROM audit.entries e");
        Assert.NotEmpty(audit);
        foreach (var secret in new[] { Sentinel, WebhookSecret })
        {
            Assert.DoesNotContain(bodies, entry => entry.Body.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(secret, StringComparison.Ordinal));
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        }
    }
}
