using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Quotations.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Infrastructure.Messaging;
using Modules.Shopify.Infrastructure.Workers;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using Flow = Modules.Shopify.IntegrationTests.ShopifyEditSyncFlowTests.Flow;
using RecordingUpdater = Modules.Shopify.IntegrationTests.ShopifyOrderPushWorkerTests.RecordingUpdater;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-11-24 §11.1: la dirección cambiada en «Editar pedido» llega a Shopify. Quotations publica
/// <c>quotations.order.shipping-party-changed.v1</c> en la transacción del pedido, el consumidor
/// <c>shopify.quotation-order-events</c> encola el envío <c>ShippingAddress</c> y el worker de envíos lo manda. Lo que publica
/// Quotations lo amarra <c>OrderEditsApiTests</c>.
/// </summary>
public sealed class ShopifyDeliveryEditPushTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Fixture(Action<JsonObject>? edit = null)
    {
        var order = (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;
        edit?.Invoke(order);
        return order;
    }

    private static async Task<Flow> StartAsync(RecordingUpdater shopify, Action<JsonObject>? order = null)
    {
        var flow = await Flow.StartAsync(services =>
        {
            services.RemoveAll<IShopifyOrderUpdater>();
            services.AddSingleton<IShopifyOrderUpdater>(shopify);
        });
        await flow.DeliverAsync("orders/create", "wh-1", Fixture(order));
        return flow;
    }

    private static Task<bool> ConsumeAsync(Flow flow) =>
        flow.Factory.Services.GetRequiredService<QuotationOrderEventsProcessor>().ProcessBatchAsync(Ct);

    private static Task DrainAsync(Flow flow) =>
        ActivatorUtilities.CreateInstance<ShopifyOrderPushWorker>(flow.Factory.Services).DrainAsync(Ct);

    // El literal del contrato, escrito a mano: renombrar un campo del lado de Quotations deja los mensajes procesados sin efecto.
    private static async Task<Guid> InsertEventAsync(Flow flow, Guid orderId)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            flow.ConnectionString,
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts, last_error)
            VALUES (@id, 'quotations.order.shipping-party-changed.v1', CAST(@payload AS jsonb), @correlation, now(), NULL, 0, NULL)
            """,
            ("id", id),
            ("payload",
                $$"""{"tenantId":"{{flow.World.TenantId}}","orderId":"{{orderId}}","source":"Shopify","occurredAt":"2026-11-25T15:00:00+00:00"}"""),
            ("correlation", orderId.ToString()));
        return id;
    }

    private static async Task<Guid> ImportedOrderIdAsync(Flow flow) => Guid.Parse(await ShopifyOrderDelivery.OrderIdAsync(flow));

    private static Task<string> PushesAsync(Flow flow) =>
        flow.ScalarAsync(
            """
            SELECT count(*) || '|' || coalesce(max(status), 'none') || '|' || coalesce(max(failure_code), 'none') || '|'
                   || coalesce(max(groups::text), 'none')
            FROM shopify.order_pushes
            """);

    private static Task<string> ProcessedAsync(Flow flow, Guid messageId) =>
        ScalarAsync<string>(
            flow.ConnectionString,
            """
            SELECT count(*)::text FROM shopify.inbox_messages
            WHERE consumer = 'shopify.quotation-order-events' AND message_id = @id AND processed_at IS NOT NULL
            """,
            ("id", messageId));

    private static async Task SaveDeliveryAsync(Flow flow, JsonObject party)
    {
        using var client = flow.Client(OrdersPermissions.OrderRead, OrdersPermissions.OrderManage);
        var response = await ShopifyOrderDelivery.PutAsync(client, flow, isStorePickup: false, party);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task TheConsumerEnqueuesAShippingAddressPush()
    {
        await using var flow = await StartAsync(new RecordingUpdater());
        var messageId = await InsertEventAsync(flow, await ImportedOrderIdAsync(flow));

        await ConsumeAsync(flow);
        await ConsumeAsync(flow);

        Assert.Equal("1|Pending|none|{ShippingAddress}", await PushesAsync(flow));
        Assert.Equal("1", await ProcessedAsync(flow, messageId));
    }

    [Fact]
    public async Task AnOpenPushIsMergedNotDuplicated()
    {
        await using var flow = await StartAsync(new RecordingUpdater());
        await ShopifyOrderPushWorkerTests.CorrectTheNoteAsync(flow, "Llamar antes");
        await InsertEventAsync(flow, await ImportedOrderIdAsync(flow));

        await ConsumeAsync(flow);

        Assert.Equal("1|Pending|none|{ShippingAddress,Note}", await PushesAsync(flow));
    }

    [Fact]
    public async Task AnInactiveConnectionHoldsThePush()
    {
        await using var flow = await StartAsync(new RecordingUpdater());
        await ShopifyOrderCorrectionApiTests.SetConnectionActiveAsync(flow, active: false);
        await InsertEventAsync(flow, await ImportedOrderIdAsync(flow));

        await ConsumeAsync(flow);

        Assert.Equal("1|Held|connection_inactive|{ShippingAddress}", await PushesAsync(flow));
    }

    // Sin fila en el espejo no hay a quién empujarle: el mensaje se da por procesado.
    [Fact]
    public async Task AnOrderWithoutMirrorRowEnqueuesNothing()
    {
        await using var flow = await StartAsync(new RecordingUpdater());
        var messageId = await InsertEventAsync(flow, Guid.CreateVersion7());

        await ConsumeAsync(flow);

        Assert.Equal("0|none|none|none", await PushesAsync(flow));
        Assert.Equal("1", await ProcessedAsync(flow, messageId));
    }

    // Como la corrección del asesor (plan D3): la proyección no se mueve, así que la edición siguiente de Shopify sobre la
    // dirección es conflicto y gana QEP.
    [Fact]
    public async Task TheProjectionDoesNotMove()
    {
        await using var flow = await StartAsync(new RecordingUpdater());
        var projection = await flow.ScalarAsync("SELECT applied_projection::text FROM shopify.orders");
        await SaveDeliveryAsync(
            flow, ShopifyOrderDelivery.SpanishParty(await ShopifyOrderDelivery.RegionIdAsync(flow, "ES", "28")));

        await ConsumeAsync(flow);
        await DrainAsync(flow);

        Assert.Equal(projection, await flow.ScalarAsync("SELECT applied_projection::text FROM shopify.orders"));
        await flow.DeliverAsync("orders/updated", "wh-2", Fixture(order =>
        {
            order["updated_at"] = "2026-10-01T10:30:00-05:00";
            order["shipping_address"]!["address1"] = "Carrera 43A # 1-50";
        }));
        Assert.Equal("Pending|ShippingAddress|Conflict|qep_changed", await flow.ScalarAsync(
            """
            SELECT e.status || '|' || (g ->> 'kind') || '|' || (g ->> 'outcome') || '|' || (g ->> 'reason')
            FROM shopify.order_edits e, jsonb_array_elements(e.groups) AS g
            """));
    }

    // De punta a punta: «Editar pedido» → outbox → consumidor → worker → orderUpdate.
    [Fact]
    public async Task ASpanishAddressIsPushedWithProvinceAndZip()
    {
        var shopify = new RecordingUpdater();
        await using var flow = await StartAsync(shopify);
        await SaveDeliveryAsync(
            flow,
            ShopifyOrderDelivery.SpanishParty(await ShopifyOrderDelivery.RegionIdAsync(flow, "ES", "28"), postalCode: "28013"));

        await ConsumeAsync(flow);
        await DrainAsync(flow);

        var address = Assert.Single(shopify.Updates).ShippingAddress!;
        Assert.Equal(("ES", "Madrid", "M", "28013"), (address.CountryCode, address.City, address.ProvinceCode, address.Zip));
        Assert.Equal(("Lucía Fernández", "Calle Mayor 1"), (address.FirstName, address.Address1));
        Assert.Equal("Sent|none|{ShippingAddress}", await flow.ScalarAsync(
            "SELECT status || '|' || coalesce(failure_code, 'none') || '|' || groups::text FROM shopify.order_pushes"));
    }

    /// <summary>El pedido como lo devuelve Shopify después del <c>orderUpdate</c> de QEP: la dirección entera como se mandó (Shopify
    /// arma <c>name</c> con <c>firstName</c> y <c>lastName</c> y pone el nombre de la provincia).</summary>
    private static JsonObject EchoOf(ShopifyAddressUpdate sent, string updatedAt, string? note = null) =>
        Fixture(order =>
        {
            order["updated_at"] = updatedAt;
            order["note"] = note;
            order["shipping_address"] = new JsonObject
            {
                ["name"] = $"{sent.FirstName} {sent.LastName}".Trim(),
                ["first_name"] = sent.FirstName,
                ["last_name"] = sent.LastName,
                ["address1"] = sent.Address1,
                ["address2"] = sent.Address2,
                ["city"] = sent.City,
                ["province"] = "Madrid",
                ["province_code"] = sent.ProvinceCode,
                ["country"] = "Spain",
                ["country_code"] = sent.CountryCode,
                ["phone"] = sent.Phone,
                ["company"] = sent.Company,
                ["zip"] = sent.Zip,
            };
        });

    // El eco de una dirección de fuera de Colombia no es un cambio: si lo fuera, cada envío dejaría un conflicto falso y la tienda
    // automática dejaría de aplicar lo siguiente.
    [Theory]
    [InlineData("28013")]
    [InlineData(null)]
    public async Task TheEchoOfAPushedSpanishAddressCreatesNoChange(string? postalCode)
    {
        var shopify = new RecordingUpdater();
        await using var flow = await StartAsync(shopify, order => order["shipping_address"]!["zip"] = "050021");
        await flow.SetEditSyncAsync("Automatic");
        await SaveDeliveryAsync(
            flow,
            ShopifyOrderDelivery.SpanishParty(await ShopifyOrderDelivery.RegionIdAsync(flow, "ES", "28"), postalCode));
        await ConsumeAsync(flow);
        await DrainAsync(flow);
        var sent = Assert.Single(shopify.Updates).ShippingAddress!;

        await flow.DeliverAsync("orders/updated", "wh-2", EchoOf(sent, "2026-10-01T10:30:00-05:00"));

        Assert.Equal("none", await flow.ScalarAsync(
            "SELECT coalesce(string_agg(status || '|' || groups::text, ','), 'none') FROM shopify.order_edits"));
        Assert.Equal("Madrid", await flow.ScalarAsync("SELECT applied_payload -> 'shippingAddress' ->> 'city' FROM shopify.orders"));

        await flow.DeliverAsync("orders/updated", "wh-3", EchoOf(sent, "2026-10-01T11:00:00-05:00", note: "Llamar antes"));

        Assert.Equal("Applied", await flow.ScalarAsync(
            "SELECT coalesce(string_agg(status, ','), 'none') FROM shopify.order_edits"));
        Assert.Equal("Llamar antes", await flow.ScalarAsync("SELECT buyer_notes FROM quotations.orders"));
        Assert.Equal("Calle Mayor 1", await flow.ScalarAsync("SELECT address FROM quotations.order_parties WHERE role = 'Shipping'"));
    }

    // Otra dirección de fuera de Colombia que no es lo que QEP mandó sigue siendo conflicto: gana QEP.
    [Fact]
    public async Task AForeignShopifyEditThatIsNotTheEchoStaysAConflict()
    {
        var shopify = new RecordingUpdater();
        await using var flow = await StartAsync(shopify);
        await flow.SetEditSyncAsync("Automatic");
        await SaveDeliveryAsync(
            flow, ShopifyOrderDelivery.SpanishParty(await ShopifyOrderDelivery.RegionIdAsync(flow, "ES", "28")));
        await ConsumeAsync(flow);
        await DrainAsync(flow);
        var edited = Assert.Single(shopify.Updates).ShippingAddress! with { Address1 = "Calle de Alcalá 20" };

        await flow.DeliverAsync("orders/updated", "wh-2", EchoOf(edited, "2026-10-01T10:30:00-05:00"));

        Assert.Equal("Pending|ShippingAddress|Conflict", await flow.ScalarAsync(
            """
            SELECT e.status || '|' || (g ->> 'kind') || '|' || (g ->> 'outcome')
            FROM shopify.order_edits e, jsonb_array_elements(e.groups) AS g
            """));
        Assert.Equal("Calle Mayor 1", await flow.ScalarAsync("SELECT address FROM quotations.order_parties WHERE role = 'Shipping'"));
    }

    // Sin código postal propio y en otra provincia, el zip del pedido de Shopify sería el de otra dirección: no viaja.
    [Fact]
    public async Task ASpanishAddressOnAnotherProvinceWithoutPostalCodeIsPushedWithoutZip()
    {
        var shopify = new RecordingUpdater();
        await using var flow = await StartAsync(shopify, order => order["shipping_address"]!["zip"] = "050021");
        await SaveDeliveryAsync(
            flow,
            ShopifyOrderDelivery.SpanishParty(await ShopifyOrderDelivery.RegionIdAsync(flow, "ES", "28"), postalCode: null));

        await ConsumeAsync(flow);
        await DrainAsync(flow);

        var address = Assert.Single(shopify.Updates).ShippingAddress!;
        Assert.Equal(("ES", "M", (string?)null), (address.CountryCode, address.ProvinceCode, address.Zip));
    }

    // El último Shopify del pedido está en España y «Editar pedido» lo devuelve a Colombia: viaja el país de la parte.
    [Fact]
    public async Task AColombianAddressOverASpanishSnapshotIsPushedAsColombia()
    {
        var shopify = new RecordingUpdater();
        await using var flow = await StartAsync(shopify);
        await ExecuteAsync(
            flow.ConnectionString,
            """UPDATE shopify.orders SET payload = jsonb_set(payload, '{shippingAddress,countryCode}', '"ES"')""");
        var envigado = (await flow.ScalarAsync("SELECT department_id::text || '|' || id::text FROM geography.cities WHERE divipola_code = '05266'"))
            .Split('|');
        await SaveDeliveryAsync(flow, new JsonObject
        {
            ["name"] = "Laura Gómez",
            ["phone"] = "+573001112233",
            ["email"] = "laura.gomez@example.com",
            ["address"] = "Calle 38 Sur # 43-20",
            ["departmentId"] = envigado[0],
            ["cityId"] = envigado[1],
        });

        await ConsumeAsync(flow);
        await DrainAsync(flow);

        var address = Assert.Single(shopify.Updates).ShippingAddress!;
        Assert.Equal(("CO", "ENVIGADO", "ANT"), (address.CountryCode, address.City, address.ProvinceCode));
    }
}
