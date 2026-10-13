using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Spec 2026-10-13 §4.5: los tres eventos del ciclo de una cotización, con el payload que Pipeline lee por nombre, y
/// sólo cuando el cambio se guardó (mismo SaveChanges). Un rechazo no deja evento.</summary>
public sealed class QuotationLifecycleEventsApiTests
{
    private const string Sent = "quotations.quotation.sent.v1";
    private const string Converted = "quotations.quotation.converted.v1";
    private const string Cancelled = "quotations.order.cancelled.v1";

    private static readonly string[] CancellerPermissions = [.. ManagerPermissions, OrdersPermissions.OrderCancel];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<OrderResponse> ConvertAsync(HttpClient client, QepApiFactory factory, Guid tenantId, QuotationResponse quotation)
    {
        var proof = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("FullPaymentReceived", null, [new OrderPaymentProofRequest(proof, quotation.Total)]),
            Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(Ct);
        Assert.NotNull(order);
        return order;
    }

    [Fact]
    public async Task SendingWritesTheSentEventWithTheContractPayload()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);

        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var message = Assert.Single(await OutboxMessagesAsync(factory, Sent));
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(tenantId, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(quotation.Id, root.GetProperty("quotationId").GetGuid());
        Assert.Equal(quotation.QuotationNumber, root.GetProperty("quotationNumber").GetString());
        Assert.Equal(clientId, root.GetProperty("clientId").GetGuid());
        Assert.Equal(quotation.AdvisorId, root.GetProperty("advisorMemberId").GetGuid());
        Assert.Equal(quotation.Total, root.GetProperty("total").GetDecimal());
        Assert.Equal(quotation.Currency, root.GetProperty("currency").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("occurredAt").ValueKind);
    }

    // Una cotización anulada no se puede enviar (422 quotation.quotation.not_draft): no cambió y el evento no existe. Una vacía
    // sin cuenta de cobro sí se envía (200), así que no sirve para ejercer el rechazo.
    [Fact]
    public async Task ARejectedSendWritesNoEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId);
        (await client.PostAsync($"{QuotationsUrl(tenantId)}/{quotation.Id}/void", content: null, Ct)).EnsureSuccessStatusCode();
        var pdf = await CreateAvailablePdfFileAsync(client, factory, tenantId);

        var response = await client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/{quotation.Id}/send", new SendQuotationRequest(pdf), Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await OutboxMessagesAsync(factory, Sent));
    }

    // Spec D25: converted lleva todo lo de sent (para enlazar una convertida que nunca se envió) más el pedido. Una segunda
    // conversión se rechaza y no deja otro evento.
    [Fact]
    public async Task ConvertingWritesTheConvertedEventWithTheOrderOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);

        var order = await ConvertAsync(client, factory, tenantId, quotation);
        var proof = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var again = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("FullPaymentReceived", null, [new OrderPaymentProofRequest(proof, quotation.Total)]),
            Ct);

        Assert.NotEqual(HttpStatusCode.Created, again.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, Converted));
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(quotation.Id, root.GetProperty("quotationId").GetGuid());
        Assert.Equal(clientId, root.GetProperty("clientId").GetGuid());
        Assert.Equal(quotation.AdvisorId, root.GetProperty("advisorMemberId").GetGuid());
        Assert.Equal(quotation.Total, root.GetProperty("total").GetDecimal());
        Assert.Equal(quotation.Currency, root.GetProperty("currency").GetString());
        Assert.Equal(order.Id, root.GetProperty("orderId").GetGuid());
        Assert.Equal(order.OrderNumber, root.GetProperty("orderNumber").GetString());
    }

    [Fact]
    public async Task CancellingWritesTheCancelledEventOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var order = await ConvertAsync(client, factory, tenantId, quotation);
        var cancelUrl = $"/api/v1/tenants/{tenantId}/orders/{order.Id}/cancel";

        var first = await client.PostAsJsonAsync(cancelUrl, new CancelOrderRequest("El cliente desistió"), Ct);
        var second = await client.PostAsJsonAsync(cancelUrl, new CancelOrderRequest("Otra vez"), Ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        var message = Assert.Single(await OutboxMessagesAsync(factory, Cancelled));
        using var payload = JsonDocument.Parse(message.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(tenantId, root.GetProperty("tenantId").GetGuid());
        Assert.Equal(order.Id, root.GetProperty("orderId").GetGuid());
        Assert.Equal(quotation.Id, root.GetProperty("quotationId").GetGuid());
    }

    // Spec 2026-10-13 §7: el evento va en el mismo SaveChanges que el Sent. Si guardar falla después de publicar, no queda ni el
    // evento ni la cotización enviada. El handler de envío envuelve la falla en quotation.send.failed (422).
    [Fact]
    public async Task ASendWhoseSaveFailsLeavesNoEventAndTheQuotationInDraft()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        // Lo que hace CreateSentQuotationAsync, sin el envío: cuenta de cobro, producto y PDF.
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var quotation = await CreateQuotationAsync(client, tenantId, clientId,
            billingAccount: new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));
        (await client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/{quotation.Id}/items", new AddQuotationItemRequest(productId, 1m), Ct))
            .EnsureSuccessStatusCode();
        var pdf = await CreateAvailablePdfFileAsync(client, factory, tenantId);
        factory.SaveFailure.FailWhenSaving = Sent;

        var response = await client.PostAsJsonAsync($"{QuotationsUrl(tenantId)}/{quotation.Id}/send", new SendQuotationRequest(pdf), Ct);

        factory.SaveFailure.FailWhenSaving = null;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("quotation.send.failed", problem.RootElement.GetProperty("code").GetString());
        Assert.Empty(await OutboxMessagesAsync(factory, Sent));
        var current = await client.GetFromJsonAsync<QuotationResponse>($"{QuotationsUrl(tenantId)}/{quotation.Id}", Ct);
        Assert.Equal("Draft", current!.Status);
    }

    // Spec 2026-10-13 §7: la conversión publica dentro de la transacción del contador. Si guardar falla, no queda evento, la
    // cotización sigue Sent y se puede convertir después (no quedó un pedido a medias).
    [Fact]
    public async Task AConversionWhoseSaveFailsLeavesNoEventAndNoOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var proof = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        factory.SaveFailure.FailWhenSaving = Converted;

        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest("FullPaymentReceived", null, [new OrderPaymentProofRequest(proof, quotation.Total)]),
            Ct);

        factory.SaveFailure.FailWhenSaving = null;
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await OutboxMessagesAsync(factory, Converted));
        var current = await client.GetFromJsonAsync<QuotationResponse>($"{QuotationsUrl(tenantId)}/{quotation.Id}", Ct);
        Assert.Equal("Sent", current!.Status);
        await ConvertAsync(client, factory, tenantId, quotation);
        Assert.Single(await OutboxMessagesAsync(factory, Converted));
    }

    // Spec 2026-10-13 §7: la anulación y su evento, juntos o ninguno. Si guardar falla, el pedido sigue sin anular y con su versión.
    [Fact]
    public async Task ACancellationWhoseSaveFailsLeavesNoEventAndTheOrderUncancelled()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var order = await ConvertAsync(client, factory, tenantId, quotation);
        factory.SaveFailure.FailWhenSaving = Cancelled;

        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders/{order.Id}/cancel", new CancelOrderRequest("El cliente desistió"), Ct);

        factory.SaveFailure.FailWhenSaving = null;
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(await OutboxMessagesAsync(factory, Cancelled));
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>($"/api/v1/tenants/{tenantId}/orders/{order.Id}", Ct);
        Assert.Equal((order.Version, (DateTimeOffset?)null), (detail!.Order.Version, detail.Order.CancelledAt));
    }
}
