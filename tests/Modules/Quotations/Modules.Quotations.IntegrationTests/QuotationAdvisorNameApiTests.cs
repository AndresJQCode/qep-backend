using System.Net;
using System.Net.Http.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// El nombre de la asesora cruza tres módulos —membresía en Tenancy, correo en Identity,
/// cotización en Quotations— por el adaptador de <c>Bootstrapper</c>, que ninguna prueba
/// unitaria ve. Éstas lo recorren punta a punta.
/// </summary>
public sealed class QuotationAdvisorNameApiTests
{
    // X-Permissions reemplaza el set por defecto del stub, así que los permisos de Tenancy para
    // leer el roster y renombrar se piden explícitos.
    private static readonly string[] Permissions =
        [.. ManagerPermissions, "advisorship.read", "advisorship.manage"];

    [Fact]
    public async Task TheQuotationDetailCarriesTheAdvisorNameOnceTheMemberIsNamed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        // La asesora es el owner, que nace sin nombre porque lo crea register-tenant y no una
        // invitación: el detalle trae sólo el correo.
        Assert.Null(quotation.AdvisorName);
        Assert.NotNull(quotation.AdvisorEmail);

        await RenameAsync(client, tenantId, quotation.AdvisorId, "Laura Gómez");

        var fetched = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);

        Assert.NotNull(fetched);
        Assert.Equal("Laura Gómez", fetched.AdvisorName);
        // D1: el detalle sigue trayendo el correo aparte; su pantalla lo sigue usando.
        Assert.Equal(quotation.AdvisorEmail, fetched.AdvisorEmail);
    }

    // La fila del listado trae un solo campo: el nombre o, mientras la membresía no tenga uno, el
    // correo (spec 2026-09-11, D1, nota del 2026-09-14).
    [Fact]
    public async Task TheQuotationListShowsTheAdvisorNameAndFallsBackToTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);

        // El owner nace sin nombre (CreateActive): la fila cae a su correo.
        Assert.NotNull(quotation.AdvisorEmail);
        Assert.Equal(
            quotation.AdvisorEmail,
            (await ListRowAsync(client, tenantId, quotation.Id)).AdvisorName);

        await RenameAsync(client, tenantId, quotation.AdvisorId, "Laura Gómez");

        Assert.Equal(
            "Laura Gómez",
            (await ListRowAsync(client, tenantId, quotation.Id)).AdvisorName);
    }

    // La fila de pedidos trae el mismo campo que la de cotizaciones: el nombre o, mientras la
    // membresía no tenga uno, el correo (spec 2026-09-11, D1, nota del 2026-09-15).
    [Fact]
    public async Task TheOrderListShowsTheAdvisorNameAndFallsBackToTheEmail()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var order = await ConvertToOrderAsync(client, tenantId, quotation.Id);

        // El owner nace sin nombre (CreateActive): la fila cae a su correo.
        Assert.NotNull(quotation.AdvisorEmail);
        Assert.Equal(
            quotation.AdvisorEmail,
            (await OrderRowAsync(client, tenantId, order.Id)).AdvisorName);

        await RenameAsync(client, tenantId, quotation.AdvisorId, "Laura Gómez");

        Assert.Equal(
            "Laura Gómez",
            (await OrderRowAsync(client, tenantId, order.Id)).AdvisorName);
    }

    private static async Task<QuotationListItemResponse> ListRowAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
    {
        var page = await client.GetFromJsonAsync<QuotationsPageResponse>(
            QuotationsUrl(tenantId), TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return Assert.Single(page.Items, item => item.Id == quotationId);
    }

    private static async Task<OrderListItemResponse> OrderRowAsync(
        HttpClient client, Guid tenantId, Guid orderId)
    {
        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"/api/v1/tenants/{tenantId}/orders", TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        return Assert.Single(page.Items, item => item.Id == orderId);
    }

    // Sin comprobantes: el pago queda pendiente, el único caso en que la conversión no los exige.
    // Esta prueba mira la fila del listado, no el asistente.
    private static async Task<OrderResponse> ConvertToOrderAsync(
        HttpClient client, Guid tenantId, Guid quotationId)
    {
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotationId}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    // display-name se retiró (spec 2026-09-24, D7); el nombre se edita por PUT .../profile,
    // que también pide el código de asesor: null lo deja sin tocar.
    private static async Task RenameAsync(
        HttpClient client, Guid tenantId, Guid membershipId, string displayName)
    {
        var version = await MembershipVersionAsync(client, tenantId, membershipId);
        using var rename = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/profile")
        {
            Content = JsonContent.Create(new { displayName, advisorCode = (int?)null })
        };
        rename.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        using var renamed = await client.SendAsync(rename, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    }

    // La versión se lee del roster en vez de suponerla: If-Match tiene que llevar la vigente.
    private static async Task<long> MembershipVersionAsync(
        HttpClient client, Guid tenantId, Guid membershipId)
    {
        var roster = await client.GetFromJsonAsync<MembershipListPayload>(
            $"/api/v1/tenants/{tenantId}/memberships", TestContext.Current.CancellationToken);
        Assert.NotNull(roster);
        return Assert.Single(roster.Items, item => item.Id == membershipId).Version;
    }

    private sealed record MembershipListPayload(IReadOnlyList<MembershipRowPayload> Items);

    private sealed record MembershipRowPayload(Guid Id, long Version);
}
