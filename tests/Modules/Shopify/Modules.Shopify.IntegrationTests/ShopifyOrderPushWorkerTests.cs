using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Quotations.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Workers;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using Flow = Modules.Shopify.IntegrationTests.ShopifyEditSyncFlowTests.Flow;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-14 §5 y plan D14: el worker reclama el envío pendiente, arma <c>orderUpdate</c> con lo que el pedido tiene ahora y lo
/// cierra <c>Sent</c>; si Shopify sigue sin responder bien en el último intento, lo cierra <c>Failed</c> con
/// <c>retries_exhausted</c>. Shopify es un <see cref="IShopifyOrderUpdater"/> falso.
/// </summary>
public sealed class ShopifyOrderPushWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static async Task CorrectTheCityToEnvigadoAsync(Flow flow)
    {
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var ids = (await flow.ScalarAsync("SELECT department_id::text || '|' || id::text FROM geography.cities WHERE divipola_code = '05266'"))
            .Split('|');
        var mirrorId = await flow.ScalarAsync("SELECT id::text FROM shopify.orders");
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tenants/{flow.World.TenantId}/shopify/orders/{mirrorId}/corrections")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["shippingParty"] = new JsonObject
                {
                    ["name"] = "Laura Gómez",
                    ["phone"] = "+573001112233",
                    ["email"] = "laura.gomez@example.com",
                    ["address"] = "Calle 10 # 43-20 Apto 301",
                    ["departmentId"] = ids[0],
                    ["cityId"] = ids[1],
                },
                ["buyerNotes"] = null,
                ["attributes"] = new JsonArray(
                    new JsonObject { ["key"] = "documento", ["value"] = "1036000111" },
                    new JsonObject { ["key"] = "tipo_documento", ["value"] = "CC" }),
            }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{await flow.ScalarAsync("SELECT version::text FROM quotations.orders")}\"");
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static JsonObject Fixture() => (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;

    private static Task DrainAsync(Flow flow) => ActivatorUtilities.CreateInstance<ShopifyOrderPushWorker>(flow.Factory.Services).DrainAsync(Ct);

    [Fact]
    public async Task ThePushIsSentAndMarkedSent()
    {
        var shopify = new RecordingUpdater();
        await using var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyOrderUpdater>();
            services.AddSingleton<IShopifyOrderUpdater>(shopify);
        });
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await CorrectTheCityToEnvigadoAsync(flow);

        await DrainAsync(flow);

        Assert.Equal("Sent|1|true", await flow.ScalarAsync(
            "SELECT status || '|' || attempts || '|' || (sent_at IS NOT NULL)::text FROM shopify.order_pushes"));
        var update = Assert.Single(shopify.Updates);
        Assert.Equal(("ENVIGADO", "ANT", "CO"), (update.ShippingAddress!.City, update.ShippingAddress.ProvinceCode, update.ShippingAddress.CountryCode));
        Assert.Equal("1036000111", update.ShippingAddress.Company);
        Assert.False(update.SetNote);
        Assert.Null(update.Attributes);
        await DrainAsync(flow);
        Assert.Single(shopify.Updates);
    }

    [Fact]
    public async Task AfterTheLastAttemptThePushIsRetriesExhausted()
    {
        var shopify = new RecordingUpdater { Result = new ShopifyOrderUpdateResult(ShopifyOrderUpdateKind.Retry, null, null) };
        await using var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyOrderUpdater>();
            services.AddSingleton<IShopifyOrderUpdater>(shopify);
        });
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await CorrectTheCityToEnvigadoAsync(flow);
        await DrainAsync(flow);
        Assert.Equal("Pending|1", await flow.ScalarAsync("SELECT status || '|' || attempts FROM shopify.order_pushes"));
        await ExecuteAsync(
            flow.ConnectionString,
            $"UPDATE shopify.order_pushes SET attempts = {ShopifyOrderPushWorker.MaxAttempts - 1}, lease_until = NULL");

        await DrainAsync(flow);

        Assert.Equal(
            $"Failed|retries_exhausted|{ShopifyOrderPushWorker.MaxAttempts}",
            await flow.ScalarAsync("SELECT status || '|' || failure_code || '|' || attempts FROM shopify.order_pushes"));
    }

    /// <summary>Anota lo que se mandaría a Shopify y responde <see cref="Result"/>.</summary>
    internal sealed class RecordingUpdater : IShopifyOrderUpdater
    {
        private readonly List<ShopifyOrderUpdate> updates = [];

        public ShopifyOrderUpdateResult Result { get; init; } = new(ShopifyOrderUpdateKind.Updated, null, null);

        public IReadOnlyList<ShopifyOrderUpdate> Updates
        {
            get
            {
                lock (updates)
                {
                    return [.. updates];
                }
            }
        }

        public Task<ShopifyOrderUpdateResult> UpdateAsync(ShopifyConnectionRef connection, ShopifyOrderUpdate update, CancellationToken cancellationToken)
        {
            lock (updates)
            {
                updates.Add(update);
            }

            return Task.FromResult(Result);
        }
    }
}
