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

    // Owner, 2026-11-14: un envío retenido no lo reclama el worker, aunque la conexión ya esté activa; sólo lo sueltan los dos
    // endpoints de sincronización.
    [Fact]
    public async Task TheWorkerNeverClaimsAHeldPush()
    {
        var shopify = new RecordingUpdater();
        await using var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyOrderUpdater>();
            services.AddSingleton<IShopifyOrderUpdater>(shopify);
        });
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await ShopifyOrderCorrectionApiTests.SetConnectionActiveAsync(flow, active: false);
        await CorrectTheNoteAsync(flow, "A");
        await ShopifyOrderCorrectionApiTests.SetConnectionActiveAsync(flow, active: true);

        await DrainAsync(flow);

        Assert.Equal("Held|0", await flow.ScalarAsync("SELECT status || '|' || attempts FROM shopify.order_pushes"));
        Assert.Empty(shopify.Updates);
    }

    // Owner, 2026-11-14: la conexión cae después de encolar; al mandar, el envío queda retenido con connection_missing, no Failed.
    [Fact]
    public async Task AConnectionThatDroppedAfterTheEnqueueHoldsThePush()
    {
        var shopify = new RecordingUpdater();
        await using var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyOrderUpdater>();
            services.AddSingleton<IShopifyOrderUpdater>(shopify);
        });
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await CorrectTheNoteAsync(flow, "A");
        await ShopifyOrderCorrectionApiTests.SetConnectionActiveAsync(flow, active: false);

        await DrainAsync(flow);

        Assert.Equal("Held|connection_missing", await flow.ScalarAsync("SELECT status || '|' || failure_code FROM shopify.order_pushes"));
        Assert.Empty(shopify.Updates);
    }

    // Review B3-B: cerrar el envío por SQL sube la versión. Una corrección que había leído el pendiente y se fusiona con él choca
    // (412) en vez de responder 200 sobre un envío que ya nadie va a mandar.
    [Fact]
    public async Task ClosingAPushAsFailedBumpsItsVersion()
    {
        await using var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyOrderUpdater>();
            services.AddSingleton<IShopifyOrderUpdater>(new RecordingUpdater());
        });
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await CorrectTheNoteAsync(flow, "A");
        var pushId = Guid.Parse(await flow.ScalarAsync("SELECT id::text FROM shopify.order_pushes"));
        var before = long.Parse(await flow.ScalarAsync("SELECT version::text FROM shopify.order_pushes"), System.Globalization.CultureInfo.InvariantCulture);

        await using (var scope = flow.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IShopifyOrderPushQueue>()
                .MarkFailedAsync(pushId, DateTimeOffset.UtcNow, ShopifyPushFailureCodes.RetriesExhausted, Ct);
        }

        Assert.Equal(
            $"Failed|{before + 1}",
            await flow.ScalarAsync("SELECT status || '|' || version FROM shopify.order_pushes"));
    }

    /// <summary>El asesor guarda sólo otra nota del cliente (la dirección no viaja; los atributos son los del fixture).</summary>
    internal static async Task CorrectTheNoteAsync(Flow flow, string note)
    {
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var mirrorId = await flow.ScalarAsync("SELECT id::text FROM shopify.orders");
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tenants/{flow.World.TenantId}/shopify/orders/{mirrorId}/corrections")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["shippingParty"] = null,
                ["buyerNotes"] = note,
                ["attributes"] = new JsonArray(
                    new JsonObject { ["key"] = "documento", ["value"] = "1036000111" },
                    new JsonObject { ["key"] = "tipo_documento", ["value"] = "CC" }),
            }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{await flow.ScalarAsync("SELECT version::text FROM quotations.orders")}\"");
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Review Important 1: el worker leyó la nota «A» y está llamando a Shopify cuando el asesor guarda «B» (el mismo grupo, así que
    // se fusiona con el envío pendiente). El MarkSent del worker tiene que chocar y el envío salir otra vez con «B»; si no, Shopify
    // se queda con «A» y el envío dice Sent.
    [Fact]
    public async Task ASameGroupCorrectionDuringTheSendIsSentAgain()
    {
        Flow? current = null;
        var shopify = new RecordingUpdater
        {
            OnUpdate = async calls =>
            {
                if (calls == 1)
                {
                    await CorrectTheNoteAsync(current!, "B");
                }
            },
        };
        await using var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyOrderUpdater>();
            services.AddSingleton<IShopifyOrderUpdater>(shopify);
        });
        current = flow;
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await CorrectTheNoteAsync(flow, "A");

        await DrainAsync(flow);

        Assert.Equal("Pending|{Note}", await flow.ScalarAsync("SELECT status || '|' || groups::text FROM shopify.order_pushes"));
        await ExecuteAsync(flow.ConnectionString, "UPDATE shopify.order_pushes SET lease_until = NULL");
        await DrainAsync(flow);

        Assert.Equal("A,B", string.Join(',', shopify.Updates.Select(update => update.Note)));
        Assert.Equal("Sent|2", await flow.ScalarAsync("SELECT status || '|' || attempts FROM shopify.order_pushes"));
    }

    // Review Minor 2: mientras se manda, el envío queda tomado por más que el timeout de shopify.admin (10 s) más las lecturas, así
    // otra réplica no lo vuelve a reclamar en vuelo; al terminar con Retry, la espera es la de la escalera (10 s el primero).
    [Fact]
    public async Task TheClaimOutlastsTheSendAndARetryFollowsTheLadder()
    {
        var shopify = new RecordingUpdater { Result = new ShopifyOrderUpdateResult(ShopifyOrderUpdateKind.Retry, null, null) };
        await using var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyOrderUpdater>();
            services.AddSingleton<IShopifyOrderUpdater>(shopify);
        });
        await flow.DeliverAsync("orders/create", "wh-1", Fixture());
        await CorrectTheNoteAsync(flow, "A");
        var pushId = Guid.Parse(await flow.ScalarAsync("SELECT id::text FROM shopify.order_pushes"));
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        await using (var scope = flow.Factory.Services.CreateAsyncScope())
        {
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IShopifyOrderPushQueue>().TryClaimAsync(pushId, now, Ct));
        }

        Assert.True(ShopifyOrderPushWorker.InFlightLease >= TimeSpan.FromMinutes(1));
        Assert.Equal(
            ((int)ShopifyOrderPushWorker.InFlightLease.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            await ScalarAsync<string>(
                flow.ConnectionString,
                "SELECT round(extract(epoch FROM lease_until - @now))::int::text FROM shopify.order_pushes",
                ("now", now)));

        await ExecuteAsync(flow.ConnectionString, "UPDATE shopify.order_pushes SET lease_until = NULL, attempts = 0");
        await DrainAsync(flow);

        var ladder = await ScalarAsync<int>(
            flow.ConnectionString, "SELECT round(extract(epoch FROM lease_until - updated_at))::int FROM shopify.order_pushes");
        Assert.InRange(ladder, 9, 15);
    }

    /// <summary>Anota lo que se mandaría a Shopify y responde <see cref="Result"/>. <see cref="OnUpdate"/> corre en cada llamada
    /// (con su número) antes de responder: es lo que pasa mientras la llamada está en vuelo.</summary>
    internal sealed class RecordingUpdater : IShopifyOrderUpdater
    {
        private readonly List<ShopifyOrderUpdate> updates = [];

        public ShopifyOrderUpdateResult Result { get; init; } = new(ShopifyOrderUpdateKind.Updated, null, null);

        public Func<int, Task>? OnUpdate { get; init; }

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

        public async Task<ShopifyOrderUpdateResult> UpdateAsync(
            ShopifyConnectionRef connection, ShopifyOrderUpdate update, CancellationToken cancellationToken)
        {
            int calls;
            lock (updates)
            {
                updates.Add(update);
                calls = updates.Count;
            }

            if (OnUpdate is { } hook)
            {
                await hook(calls);
            }

            return Result;
        }
    }
}
