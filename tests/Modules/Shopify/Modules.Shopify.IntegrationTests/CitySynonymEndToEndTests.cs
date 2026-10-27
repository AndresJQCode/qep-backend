using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Modules.Geography.Application;
using Modules.Shopify.Infrastructure.Messaging;
using Modules.Shopify.Infrastructure.Workers;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using CalliShop = Modules.Shopify.IntegrationTests.ShopifyCityCorrectionFlowTests.CalliShop;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-26 SC-14, de punta a punta contra Postgres: lo que el operador hace por la consola de ciudades
/// (<c>resolve</c>, <c>PUT</c>, <c>DELETE</c>) atraviesa el outbox de plataforma, el consumidor <c>shopify.city-synonym-events</c>
/// y el worker de correcciones hasta el pedido y el cliente reales. Las pruebas no llaman a los workers por su temporizador: pasan
/// un tick del consumidor (<see cref="CitySynonymEventsProcessor.ProcessBatchAsync"/>) y una pasada del worker
/// (<c>CalliShop.DrainCorrectionsAsync</c>), las mismas que ya usan <c>ShopifyCityCorrectionFlowTests</c> y
/// <c>CityOperatorImpactFlowTests</c>. Son pruebas de caracterización: escritas sobre el código de SC-01..SC-13.
/// </summary>
public sealed class CitySynonymEndToEndTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");
    private static readonly Guid OperatorSubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");

    private static string Operator => $"/api/v1/tenants/{OperatorTenantId}/operator/cities";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // El recorrido completo del §4.4: «CALLI» llegó por el webhook y quedó en la bandeja; el operador lo confirma como Cali; el
    // consumidor siembra los trabajos y el worker corrige el pedido (parte de envío en Cali, notas sin la dirección cruda) y completa
    // al cliente que sólo le faltaba la ciudad. Un pedido nuevo con «Calli» ya entra casado por la importación.
    [Fact]
    public async Task CalliFromTheWebhookToTheCorrectedOrderAndCustomer()
    {
        await using var shop = await CalliShop.StartAsync(operatorTenantId: OperatorTenantId);
        // CalliShop deja el sinónimo CALLI escrito por SQL: se retira para confirmarlo de verdad por la consola.
        await ExecuteAsync(shop.ConnectionString, "DELETE FROM geography.city_synonyms WHERE id = @id", ("id", shop.SynonymId));
        var rowId = await ScalarAsync<Guid>(
            shop.ConnectionString, "SELECT id FROM geography.unresolved_cities WHERE normalized_text = 'CALLI'");
        using var client = OperatorClient(shop);

        var resolved = await client.PostAsJsonAsync($"{Operator}/unresolved/{rowId}/resolve", new { cityId = shop.CaliId }, Ct);

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var synonym = (await resolved.Content.ReadFromJsonAsync<CitySynonymDto>(Ct))!;
        Assert.Equal(shop.CaliId, synonym.City.Id);
        await DrainAsync(shop);

        Assert.Equal(
            "Applied|Applied",
            await ScalarAsync<string>(
                shop.ConnectionString,
                "SELECT string_agg(status, '|' ORDER BY target) FROM shopify.city_correction_jobs WHERE synonym_id = @synonym",
                ("synonym", synonym.Id)));
        Assert.Equal(
            $"{shop.CaliId}|false",
            await ScalarAsync<string>(
                shop.ConnectionString,
                """
                SELECT p.city_id || '|' || (o.notes LIKE '%Dirección de envío de Shopify: %')
                FROM quotations.orders o JOIN quotations.order_parties p ON p.order_id = o.id AND p.role = 'Shipping'
                WHERE o.id = @order AND o.tenant_id = @tenant
                """,
                ("order", shop.OrderId),
                ("tenant", shop.World.TenantId)));
        Assert.Equal($"Complete|{shop.CaliId}|2", await shop.CustomerAsync());
        // El CUC termina en el código del departamento (76, Valle) y el consecutivo de seis dígitos (CucFormatter).
        Assert.True(await ScalarAsync<bool>(
            shop.ConnectionString, "SELECT cuc ~ '76[0-9]{6}$' FROM customers.customers WHERE id = @id", ("id", shop.CustomerId)));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                shop.ConnectionString,
                """
                SELECT count(*) FROM audit.entries
                WHERE tenant_id = @tenant AND action IN ('shopify.order.city_resolved', 'shopify.customer.city_resolved')
                """,
                ("tenant", shop.World.TenantId)));

        // Con el sinónimo vivo, el pedido siguiente con «Calli» (otra mayúscula) entra con la parte de envío en Cali y deja su
        // huella con origen de la importación.
        var orderId = await ImportOrderAsync(shop, 5550000000003, "#1003", "wh-calli-3", "Calli");

        Assert.Equal(
            $"{shop.CaliId}",
            await ScalarAsync<string>(
                shop.ConnectionString,
                "SELECT city_id::text FROM quotations.order_parties WHERE order_id = @order AND role = 'Shipping'",
                ("order", orderId)));
        Assert.Equal(
            "Import",
            await ScalarAsync<string>(
                shop.ConnectionString,
                "SELECT origin FROM shopify.city_corrections WHERE tenant_id = @tenant AND synonym_id = @synonym AND target = 'Order' AND target_id = @order",
                ("tenant", shop.World.TenantId),
                ("synonym", synonym.Id),
                ("order", orderId)));
    }

    // §4.6: el sinónimo apunta a CALIMA y corrigió dos pedidos; uno se aprueba. El impacto de pasarlo a CALI cuenta el pendiente,
    // lista el aprobado entre los que no se tocan, y al confirmar el PUT sólo el pendiente cambia de ciudad: el aprobado salta con
    // order_not_pending y se queda en CALIMA.
    [Fact]
    public async Task CorrectingCalimaToCaliMovesThePendingAndListsTheApproved()
    {
        await using var shop = await CalliShop.StartAsync(withSibling: true, operatorTenantId: OperatorTenantId);
        var calimaId = await ScalarAsync<Guid>(shop.ConnectionString, "SELECT id FROM geography.cities WHERE divipola_code = '76126'");
        await ExecuteAsync(
            shop.ConnectionString, "UPDATE geography.city_synonyms SET city_id = @city WHERE id = @id", ("city", calimaId), ("id", shop.SynonymId));
        await shop.SeedResolveAsync();
        await shop.SeedResolveAsync(shop.SiblingOrderId, shop.SiblingMirrorId);
        await shop.DrainCorrectionsAsync();
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                shop.ConnectionString,
                "SELECT count(*) FROM quotations.order_parties WHERE role = 'Shipping' AND city_id = @city",
                ("city", calimaId)));
        await ExecuteAsync(
            shop.ConnectionString, "UPDATE quotations.orders SET status = 'Approved' WHERE id = @order", ("order", shop.SiblingOrderId));
        using var client = OperatorClient(shop);

        var impact = await client.PostAsJsonAsync($"{Operator}/synonyms/{shop.SynonymId}/impact", new { cityId = shop.CaliId }, Ct);

        Assert.Equal(HttpStatusCode.OK, impact.StatusCode);
        var preview = (await impact.Content.ReadFromJsonAsync<CorrectImpactDto>(Ct))!;
        Assert.Equal((calimaId, shop.CaliId), (preview.From.Id, preview.To.Id));
        Assert.Equal((1, 0), (preview.OrdersToChange.Count, preview.OrdersEdited.Count));
        var notPending = Assert.Single(preview.OrdersNotPending.Orders);
        Assert.Equal((shop.World.TenantId, shop.SiblingOrderId, "Approved"), (notPending.TenantId, notPending.OrderId, notPending.Status));

        var corrected = await client.PutAsJsonAsync(
            $"{Operator}/synonyms/{shop.SynonymId}", new { cityId = shop.CaliId, expectedVersion = 1 }, Ct);

        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(shop.CaliId, (await corrected.Content.ReadFromJsonAsync<CitySynonymDto>(Ct))!.City.Id);
        await DrainAsync(shop);

        Assert.Equal(shop.CaliId, await ShippingCityAsync(shop, shop.OrderId));
        Assert.Equal(calimaId, await ShippingCityAsync(shop, shop.SiblingOrderId));
        Assert.Equal(
            "Applied",
            await RecorrectJobAsync(shop, shop.OrderId));
        Assert.Equal("Skipped|order_not_pending", await RecorrectJobAsync(shop, shop.SiblingOrderId));
    }

    // Review Focus 5: el operador confirma y borra el sinónimo antes de que el consumidor y el worker corran. Los trabajos se
    // siembran igual (el borrado no publica nada) y el worker los salta con synonym_deleted sin tocar el pedido.
    [Fact]
    public async Task DeletingWhileJobsArePendingSkipsThem()
    {
        await using var shop = await CalliShop.StartAsync(operatorTenantId: OperatorTenantId);
        await ExecuteAsync(shop.ConnectionString, "DELETE FROM geography.city_synonyms WHERE id = @id", ("id", shop.SynonymId));
        var rowId = await ScalarAsync<Guid>(
            shop.ConnectionString, "SELECT id FROM geography.unresolved_cities WHERE normalized_text = 'CALLI'");
        using var client = OperatorClient(shop);
        var resolved = await client.PostAsJsonAsync($"{Operator}/unresolved/{rowId}/resolve", new { cityId = shop.CaliId }, Ct);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        var synonym = (await resolved.Content.ReadFromJsonAsync<CitySynonymDto>(Ct))!;

        var deleted = await client.DeleteAsync($"{Operator}/synonyms/{synonym.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await DrainAsync(shop);

        Assert.Equal(
            "Skipped|synonym_deleted",
            await ScalarAsync<string>(
                shop.ConnectionString,
                """
                SELECT string_agg(DISTINCT status || '|' || coalesce(skip_reason, 'none'), ',')
                FROM shopify.city_correction_jobs WHERE synonym_id = @synonym
                """,
                ("synonym", synonym.Id)));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                shop.ConnectionString,
                "SELECT count(*) FROM quotations.order_parties WHERE order_id = @order AND role = 'Shipping'",
                ("order", shop.OrderId)));
        Assert.StartsWith("Incomplete|", await shop.CustomerAsync(), StringComparison.Ordinal);
    }

    private static HttpClient OperatorClient(CalliShop shop) =>
        CreateClient(
            shop.Factory, OperatorSubjectId, OperatorTenantId,
            GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage);

    // Un tick del consumidor (outbox → trabajos) y una pasada del worker (trabajos → pedidos y clientes).
    private static async Task DrainAsync(CalliShop shop)
    {
        await shop.Factory.Services.GetRequiredService<CitySynonymEventsProcessor>().ProcessBatchAsync(Ct);
        await shop.DrainCorrectionsAsync();
    }

    // El trabajo de recorrección de un pedido: status, y con él el motivo si se saltó.
    private static Task<string> RecorrectJobAsync(CalliShop shop, Guid orderId) =>
        ScalarAsync<string>(
            shop.ConnectionString,
            """
            SELECT status || coalesce('|' || skip_reason, '')
            FROM shopify.city_correction_jobs WHERE synonym_id = @synonym AND kind = 'Recorrect' AND target_id = @order
            """,
            ("synonym", shop.SynonymId),
            ("order", orderId));

    private static Task<Guid> ShippingCityAsync(CalliShop shop, Guid orderId) =>
        ScalarAsync<Guid>(
            shop.ConnectionString,
            "SELECT city_id FROM quotations.order_parties WHERE order_id = @order AND role = 'Shipping'",
            ("order", orderId));

    // Importa por el webhook, como CalliShop, un pedido nuevo con la ciudad dada; devuelve el id del pedido de QEP.
    private static async Task<Guid> ImportOrderAsync(CalliShop shop, long shopifyOrderId, string name, string webhookId, string city)
    {
        var order = (JsonObject)JsonNode.Parse(OrderFixture("taxes-included"))!;
        order["id"] = shopifyOrderId;
        order["admin_graphql_api_id"] = $"gid://shopify/Order/{shopifyOrderId}";
        order["name"] = name;
        var address = order["shipping_address"]!.AsObject();
        address["city"] = city;
        address["province"] = "Valle del Cauca";
        address["province_code"] = "VAC";
        using var webhooks = shop.Factory.CreateClient();
        var response = await PostWebhookAsync(webhooks, "orders/create", webhookId, Encoding.UTF8.GetBytes(order.ToJsonString()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ActivatorUtilities.CreateInstance<ShopifyWebhookDeliveryWorker>(shop.Factory.Services).DrainAsync(Ct);

        var imported = (await ScalarAsync<string>(
            shop.ConnectionString,
            "SELECT qep_order_id || '|' || import_state FROM shopify.orders WHERE shopify_order_id = @id",
            ("id", shopifyOrderId))).Split('|');
        Assert.Equal("Imported", imported[1]);
        return Guid.Parse(imported[0]);
    }
}
