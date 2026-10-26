using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Geography.Application;
using static Modules.Shopify.IntegrationTests.ShopifyApiHarness;
using CalliShop = Modules.Shopify.IntegrationTests.ShopifyCityCorrectionFlowTests.CalliShop;

namespace Modules.Shopify.IntegrationTests;

/// <summary>
/// Spec 2026-10-26 D-C12 contra Postgres, de punta a punta: un pedido «CALLI» que ya estaba importado antes de existir la bandeja
/// (sin ocurrencia, sin fila, sin sinónimo) llega a la bandeja una sola vez por <c>POST …/operator/cities/backfill</c>. El pedido y
/// el cliente son los reales que importó el webhook; el operador es el tenant simulado de <c>CityOperatorApiTests</c>.
/// </summary>
public sealed class ShopifyCityBackfillTests
{
    private static readonly Guid OperatorTenantId = Guid.Parse("01900000-0000-7000-8000-00000000c0de");
    private static readonly Guid OperatorSubjectId = Guid.Parse("01900000-0000-7000-8000-0000000000aa");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BackfillRoute => $"/api/v1/tenants/{OperatorTenantId}/operator/cities/backfill";

    // CalliShop importa el pedido con la bandeja de hoy (que ya lo reporta) y confirma el sinónimo por SQL. La historia previa es
    // lo que había antes: se borra la bandeja y el sinónimo, y queda el pedido Pending, sin parte de envío y con la dirección cruda
    // en las notas.
    [Fact]
    public async Task AHistoricalOrderLandsInTheBandejaOnceThroughTheOperatorEndpoint()
    {
        await using var shop = await CalliShop.StartAsync(operatorTenantId: OperatorTenantId);
        await ExecuteAsync(shop.ConnectionString, "DELETE FROM geography.unresolved_city_occurrences");
        await ExecuteAsync(shop.ConnectionString, "DELETE FROM geography.unresolved_cities");
        await ExecuteAsync(shop.ConnectionString, "DELETE FROM geography.city_synonyms");
        using var client = CreateClient(
            shop.Factory, OperatorSubjectId, OperatorTenantId,
            GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage);

        var first = await client.PostAsync(BackfillRoute, null, Ct);
        var second = await client.PostAsync(BackfillRoute, null, Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(new CityBackfillResultDto(Scanned: 1, Reported: 1, AlreadyReported: 0, NowMatching: 0, Skipped: 0), await first.Content.ReadFromJsonAsync<CityBackfillResultDto>(Ct));
        Assert.Equal(new CityBackfillResultDto(Scanned: 1, Reported: 0, AlreadyReported: 1, NowMatching: 0, Skipped: 0), await second.Content.ReadFromJsonAsync<CityBackfillResultDto>(Ct));

        // Una sola fila Pending y una sola ocurrencia, con la fila del espejo como referencia y los datos del importador.
        Assert.Equal(
            "CALLI|Pending|1",
            await ScalarAsync<string>(
                shop.ConnectionString,
                "SELECT normalized_text || '|' || status || '|' || (SELECT count(*) FROM geography.unresolved_city_occurrences) FROM geography.unresolved_cities"));
        // Sin cliente (ruling P15/S4): el recorrido no sabe si el cliente incompleto lo creó esa importación, aunque CalliShop
        // tenga uno incompleto en versión 1.
        Assert.Equal(
            $"{shop.World.TenantId}|{shop.OrderId}|none|true|false",
            await ScalarAsync<string>(
                shop.ConnectionString,
                "SELECT tenant_id || '|' || order_id || '|' || coalesce(customer_id::text, 'none') || '|' || party_city_only || '|' || customer_city_only FROM geography.unresolved_city_occurrences WHERE source = 'shopify' AND source_ref = @mirror",
                ("mirror", shop.MirrorId)));
        // La muestra es la dirección, sin el nombre ni el teléfono del comprador (Task 6).
        var sample = await ScalarAsync<string>(shop.ConnectionString, "SELECT sample_address FROM geography.unresolved_cities");
        Assert.Contains("CALLI", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("+57", sample, StringComparison.Ordinal);

        // Cada corrida deja su auditoría en el tenant operador, con los conteos.
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                shop.ConnectionString,
                "SELECT count(*) FROM audit.entries WHERE source = 'geography' AND action = @action AND tenant_id = @tenant",
                ("action", GeographyAuditActions.BackfillRan),
                ("tenant", OperatorTenantId)));
        using var counts = JsonDocument.Parse(await ScalarAsync<string>(
            shop.ConnectionString,
            "SELECT changed_fields::text FROM audit.entries WHERE action = @action ORDER BY occurred_at LIMIT 1",
            ("action", GeographyAuditActions.BackfillRan)));
        Assert.Equal(1, counts.RootElement.GetProperty("reported").GetInt32());
    }

    // El pedido que ya no cumple (aprobado) y el que ahora casa por el sinónimo no se reportan.
    [Fact]
    public async Task AnApprovedOrderAndACityThatNowMatchesAreNotReported()
    {
        await using var shop = await CalliShop.StartAsync(withSibling: true, operatorTenantId: OperatorTenantId);
        await ExecuteAsync(shop.ConnectionString, "DELETE FROM geography.unresolved_city_occurrences");
        await ExecuteAsync(shop.ConnectionString, "DELETE FROM geography.unresolved_cities");
        // El hermano se aprueba; el sinónimo CALLI → Cali que dejó CalliShop sigue vivo, así que el otro pedido ahora casa.
        await ExecuteAsync(
            shop.ConnectionString, "UPDATE quotations.orders SET status = 'Approved' WHERE id = @order", ("order", shop.SiblingOrderId));
        using var client = CreateClient(
            shop.Factory, OperatorSubjectId, OperatorTenantId,
            GeographyOperatorPermissions.CitiesRead, GeographyOperatorPermissions.CitiesManage);

        var response = await client.PostAsync(BackfillRoute, null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            new CityBackfillResultDto(Scanned: 2, Reported: 0, AlreadyReported: 0, NowMatching: 1, Skipped: 1),
            await response.Content.ReadFromJsonAsync<CityBackfillResultDto>(Ct));
        Assert.Equal(0L, await ScalarAsync<long>(shop.ConnectionString, "SELECT count(*) FROM geography.unresolved_city_occurrences"));
    }

    [Fact]
    public async Task TheBackfillIs403WithoutTheManagePermission()
    {
        await using var shop = await CalliShop.StartAsync(operatorTenantId: OperatorTenantId);
        using var readOnly = CreateClient(shop.Factory, OperatorSubjectId, OperatorTenantId, GeographyOperatorPermissions.CitiesRead);

        var response = await readOnly.PostAsync(BackfillRoute, null, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
