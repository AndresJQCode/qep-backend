using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// <c>POST /orders/{orderId}/send</c>: «Enviar el pedido por WhatsApp». El host de pruebas no configura
/// Zenvia, así que el envío sale por <c>LogWhatsAppSender</c>; el PDF lo dibuja el doble del harness
/// (<see cref="StubPdfRenderer"/>) y la copia pública la publica <c>StubPdfStorage</c>, como en el
/// envío de la cotización.
/// </summary>
public sealed class OrderSendApiTests
{
    private static string SendUrl(Guid tenantId, Guid orderId) =>
        $"/api/v1/tenants/{tenantId}/orders/{orderId}/send";

    private static string ConvertUrl(Guid tenantId, Guid quotationId) =>
        $"{QuotationsUrl(tenantId)}/{quotationId}/order";

    private static readonly string[] CancellerPermissions =
        [.. ManagerPermissions, OrdersPermissions.OrderCancel];

    // La pantalla repinta el historial con la respuesta: el detalle entero, con la entrada nueva. El
    // nombre del evento viaja tal cual (`Sent`), como el resto del historial.
    [Fact]
    public async Task SendAnswersTheDetailWithASentHistoryEntry()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest("Customer"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            var sent = json.RootElement.GetProperty("history").EnumerateArray()
                .Single(entry => entry.GetProperty("eventType").GetString() == "Sent");
            Assert.Equal("Enviado por WhatsApp al cliente.", sent.GetProperty("details").GetString());
            Assert.Equal(JsonValueKind.String, sent.GetProperty("memberName").ValueKind);
            Assert.Equal(order.Id, json.RootElement.GetProperty("order").GetProperty("id").GetGuid());
        }

        // El documento que salió es el del pedido, del mismo layout que la cotización.
        var document = factory.PdfRenderer.LastDocument;
        Assert.NotNull(document);
        Assert.Equal((QuotationPdfKind.Order, order.OrderNumber), (document.Kind, document.QuotationNumber));
        Assert.Null(document.ValidUntil);

        // Enviar no toca el pedido: misma versión, mismo estado.
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal((order.Version, order.Status), (detail.Order.Version, detail.Order.Status));
        Assert.Contains(detail.History, entry => entry.EventType == "Sent");

        var actions = (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1")).Select(ActionOf);
        Assert.Contains("quotation.order.sent", actions);
    }

    // La lección de `SendWorksWithoutABody` en la cotización: un cuerpo inferido como requerido mata el
    // envío con 500. Sin cuerpo va al cliente.
    [Fact]
    public async Task SendWorksWithoutABody()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Contains(detail.History, entry => entry.EventType == "Sent");
    }

    [Fact]
    public async Task SendingACancelledOrderIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);
        (await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}/cancel",
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.PostAsync(
            SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(response, "order.order.already_cancelled");
    }

    [Fact]
    public async Task SendingToBillingWithoutABillingPartyIsUnprocessable()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);

        var billing = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest("Billing"), TestContext.Current.CancellationToken);
        var invalid = await client.PostAsJsonAsync(
            SendUrl(tenantId, order.Id), new SendOrderRequest("Whoever"), TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(billing, "order.order.billing_phone_missing");
        await AssertDomainRejectionAsync(invalid, "order.order.recipient_invalid");
    }

    // La política del endpoint: sin `quotations.order.manage` no se envía, aunque se pueda leer el pedido.
    [Fact]
    public async Task SendingWithoutTheManagePermissionIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var order = await CreateOrderAsync(client, factory, tenantId);
        using var reader = CreateClient(
            factory,
            ownerUserId.ToString(),
            tenantId.ToString(),
            [.. ManagerPermissions.Where(permission => permission != OrdersPermissions.OrderManage)]);

        var response = await reader.PostAsync(
            SendUrl(tenantId, order.Id), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.DoesNotContain(detail.History, entry => entry.EventType == "Sent");
    }

    [Fact]
    public async Task SendingAnUnknownOrderIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;

        var response = await client.PostAsync(
            SendUrl(tenantId, Guid.CreateVersion7()), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.order.not_found", body, StringComparison.Ordinal);
    }

    private static async Task<OrderResponse> CreateOrderAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var response = await client.PostAsJsonAsync(
            ConvertUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var detail = await response.Content.ReadFromJsonAsync<OrderDetailResponse>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail.Order;
    }

    private static async Task AssertDomainRejectionAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }
}
