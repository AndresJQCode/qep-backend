using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// La entrega por envío en el pedido (spec 2026-10-17 §5.4 y §6): el pedido manual y su cálculo previo, la
/// conversión que copia el envío de la cotización y la edición del pedido pendiente, contra Postgres real.
/// </summary>
public sealed class OrderShippingApiTests
{
    private const string SettingsRead = "tenancy.settings.read";
    private const string SettingsUpdate = "tenancy.settings.update";

    private static readonly string[] Permissions = [.. ManagerPermissions, SettingsRead, SettingsUpdate];

    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    private static string OrderByIdUrl(Guid tenantId, Guid orderId) => $"{OrdersUrl(tenantId)}/{orderId}";

    private static string QuotationUrl(Guid tenantId, Guid quotationId) => $"{QuotationsUrl(tenantId)}/{quotationId}";

    /// <summary>Spec 2026-10-17 §8: el contrato del evento no gana campos. El <c>total</c> de
    /// <c>order.created.v1</c> es el del pedido, que ya incluye el envío cobrado; Pipeline lo lee por nombre.</summary>
    [Fact]
    public async Task TheOrderCreatedEventTotalIncludesTheChargedFreight()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var request = await ManualRequestAsync(client, tenantId, new ShippingRequest(carrierId, "Charged", 15000m, false));

        using var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        var order = (await ReadDetailAsync(response, HttpStatusCode.Created)).Order;
        var created = Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        using var payload = JsonDocument.Parse(created.PayloadJson);
        Assert.Equal(order.Total, payload.RootElement.GetProperty("total").GetDecimal());
        Assert.Equal(115_000m, order.Total);
        Assert.False(payload.RootElement.TryGetProperty("freightSubtotal", out var extra));
    }

    [Fact]
    public async Task CreatesAManualOrderWithFreight()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var request = await ManualRequestAsync(client, tenantId, new ShippingRequest(carrierId, "Charged", 15000m, false));

        using var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        var created = await ReadDetailAsync(response, HttpStatusCode.Created);
        AssertCharged(created.Order);
        AssertCharged((await GetDetailAsync(client, tenantId, created.Order.Id)).Order);

        var page = await client.GetFromJsonAsync<OrdersPageResponse>(OrdersUrl(tenantId), TestContext.Current.CancellationToken);
        var row = Assert.Single(page!.Items);
        Assert.Equal(115_000m, row.Total);
        Assert.Equal("Coordinadora", row.Shipping.Carrier?.Name);
        Assert.Equal("Charged", row.Shipping.ChargeMode);
        Assert.Equal(12605.04m, row.Shipping.FreightSubtotal);
        Assert.False(row.Shipping.CashOnDelivery);

        // El cálculo previo precia el mismo envío sin guardar.
        using var preview = await client.PostAsJsonAsync(
            $"{OrdersUrl(tenantId)}/preview", request, TestContext.Current.CancellationToken);
        AssertCharged((await ReadDetailAsync(preview, HttpStatusCode.OK)).Order);

        void AssertCharged(OrderResponse order)
        {
            Assert.Equal(100_000m, order.Subtotal);
            Assert.Equal(12605.04m, order.FreightSubtotal);
            Assert.Equal(2394.96m, order.FreightTaxAmount);
            Assert.Equal(115_000m, order.Total);
            Assert.Equal(115_000m, order.NetTotal);
            Assert.Equal(carrierId, order.Shipping.Carrier?.Id);
            Assert.Equal("Charged", order.Shipping.ChargeMode);
            Assert.Equal(15000m, order.Shipping.FreightAmount);
            Assert.Equal(19, order.Shipping.FreightTaxPercentage);
        }
    }

    [Fact]
    public async Task ManualOrderWithCashOnDeliveryMustBePending()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var request = (await ManualRequestAsync(client, tenantId, new ShippingRequest(carrierId, null, 0m, true))) with
        {
            PaymentStatus = "FullPaymentReceived",
            PaymentProofs = [new OrderPaymentProofRequest(proofFileId, 100_000m)],
        };

        using var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        await AssertCodeAsync(response, "order.order.cash_on_delivery_pending_only");

        // Pendiente sí: la transportadora cobra en la puerta.
        using var pending = await client.PostAsJsonAsync(
            OrdersUrl(tenantId),
            request with { PaymentStatus = "PaymentPending", PaymentProofs = [] },
            TestContext.Current.CancellationToken);
        var created = await ReadDetailAsync(pending, HttpStatusCode.Created);
        Assert.True(created.Order.Shipping.CashOnDelivery);
        Assert.Equal("PaymentPending", created.Order.PaymentStatus);
    }

    [Fact]
    public async Task ConvertingCopiesTheShippingAndTheTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var quotation = await QuotationWithShippingAsync(
            client, factory, tenantId, new ShippingRequest(carrierId, "Charged", 15000m, false));
        Assert.Equal(115_000m, quotation.Total);

        using var response = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);

        var order = (await ReadDetailAsync(response, HttpStatusCode.Created)).Order;
        Assert.Equal(quotation.Total, order.Total);
        Assert.Equal(quotation.NetTotal, order.NetTotal);
        Assert.Equal(quotation.FreightSubtotal, order.FreightSubtotal);
        Assert.Equal(quotation.FreightTaxAmount, order.FreightTaxAmount);
        Assert.Equal(carrierId, order.Shipping.Carrier?.Id);
        Assert.Equal("Coordinadora", order.Shipping.Carrier?.Name);
        Assert.Equal("Charged", order.Shipping.ChargeMode);
        Assert.Equal(15000m, order.Shipping.FreightAmount);
        Assert.Equal(19, order.Shipping.FreightTaxPercentage);
        Assert.Equal(12605.04m, order.Shipping.FreightSubtotal);
        Assert.Equal(2394.96m, order.Shipping.FreightTaxAmount);
    }

    [Fact]
    public async Task ConvertingACashOnDeliveryQuotationAsPaidIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var quotation = await QuotationWithShippingAsync(
            client, factory, tenantId, new ShippingRequest(carrierId, "OnDelivery", 20000m, true));
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);

        using var response = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/order",
            new ConvertQuotationToOrderRequest(
                "FullPaymentReceived", null, [new OrderPaymentProofRequest(proofFileId, 100_000m)]),
            TestContext.Current.CancellationToken);

        await AssertCodeAsync(response, "order.order.cash_on_delivery_pending_only");
    }

    [Fact]
    public async Task EditsTheShippingOfAPendingOrderAndRecalculatesThePayment()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var paid = await PaidOrderAsync(client, factory, tenantId);
        Assert.Equal("FullPaymentReceived", paid.Order.PaymentStatus);

        var body = EditsFor(paid, new ShippingRequest(carrierId, "Charged", 15000m, false));

        // El cálculo previo muestra el mismo efecto sin guardar ni mover la versión.
        using var preview = await client.PostAsJsonAsync(
            $"{OrderByIdUrl(tenantId, paid.Order.Id)}/preview", body, TestContext.Current.CancellationToken);
        var previewed = (await ReadDetailAsync(preview, HttpStatusCode.OK)).Order;
        Assert.Equal(115_000m, previewed.NetTotal);
        Assert.Equal("PartialPaymentReceived", previewed.PaymentStatus);
        Assert.Equal(paid.Order.Version, previewed.Version);

        using var response = await PutEditsAsync(client, tenantId, paid.Order, body);

        var saved = (await ReadDetailAsync(response, HttpStatusCode.OK)).Order;
        Assert.Equal(115_000m, saved.Total);
        Assert.Equal(115_000m, saved.NetTotal);
        Assert.Equal("Charged", saved.Shipping.ChargeMode);
        Assert.Equal("PartialPaymentReceived", saved.PaymentStatus);
        var fetched = (await GetDetailAsync(client, tenantId, paid.Order.Id)).Order;
        Assert.Equal("PartialPaymentReceived", fetched.PaymentStatus);
        Assert.Equal(12605.04m, fetched.FreightSubtotal);

        // Prender la contraentrega en un pedido que ya tiene comprobantes: el dinero ya llegó (D-F14).
        using var cashOnDelivery = await PutEditsAsync(
            client, tenantId, saved, EditsFor(paid, new ShippingRequest(carrierId, "Charged", 15000m, true)));
        await AssertCodeAsync(cashOnDelivery, "order.order.cash_on_delivery_pending_only");
    }

    [Fact]
    public async Task EditingTheShippingWritesOrderHistory()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var carrierId = await PrepareFreightAsync(client, tenantId);
        var pending = await PendingOrderAsync(client, factory, tenantId);

        using var response = await PutEditsAsync(
            client, tenantId, pending.Order, EditsFor(pending, new ShippingRequest(carrierId, "Charged", 15000m, false)));
        var saved = await ReadDetailAsync(response, HttpStatusCode.OK);
        Assert.True(saved.Order.Version > pending.Order.Version);

        var detail = await GetDetailAsync(client, tenantId, pending.Order.Id);
        var entry = Assert.Single(detail.History, entry => entry.EventType == "PartiesChanged");
        Assert.Contains("transportadora (Coordinadora)", entry.Details, StringComparison.Ordinal);
        Assert.Contains("cobro del envío (cobrado)", entry.Details, StringComparison.Ordinal);
        Assert.Contains("valor del envío (15000)", entry.Details, StringComparison.Ordinal);
        Assert.Single(await ShippingAuditsAsync(factory));

        // Sin `shipping`, como un frontend viejo: el envío queda como estaba y no se escribe historial.
        using var unchanged = await PutEditsAsync(client, tenantId, saved.Order, EditsFor(pending, shipping: null));
        var kept = (await ReadDetailAsync(unchanged, HttpStatusCode.OK)).Order;
        Assert.Equal("Charged", kept.Shipping.ChargeMode);
        Assert.Equal(saved.Order.Version, kept.Version);
        Assert.Single((await GetDetailAsync(client, tenantId, pending.Order.Id)).History, entry => entry.EventType == "PartiesChanged");
        Assert.Single(await ShippingAuditsAsync(factory));
    }

    [Fact]
    public async Task ManualOrderWithoutShippingStillWorks()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, client) = await RegisterTenantAsync(factory, Permissions);
        using var _ = client;
        var request = await ManualRequestAsync(client, tenantId, shipping: null);

        using var response = await client.PostAsJsonAsync(OrdersUrl(tenantId), request, TestContext.Current.CancellationToken);

        var order = (await ReadDetailAsync(response, HttpStatusCode.Created)).Order;
        Assert.Equal(100_000m, order.Total);
        Assert.Equal(0m, order.FreightSubtotal);
        Assert.Equal(0m, order.FreightTaxAmount);
        Assert.False(order.Shipping.IsStorePickup);
        Assert.Null(order.Shipping.Carrier);
        Assert.Null(order.Shipping.ChargeMode);
        Assert.False(order.Shipping.CashOnDelivery);
    }

    /// <summary>La tasa del 19 % como IVA del envío y la transportadora «Coordinadora», que hace recaudo.</summary>
    private static async Task<Guid> PrepareFreightAsync(HttpClient client, Guid tenantId)
    {
        var rateId = await CreateTaxRateAsync(client, tenantId, "IVA 19", 19);
        using var rate = await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/carriers/freight-tax-rate",
            new { taxRateId = rateId },
            TestContext.Current.CancellationToken);
        rate.EnsureSuccessStatusCode();

        using var carrier = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/carriers",
            new { name = "Coordinadora", externalNames = Array.Empty<string>(), supportsCashOnDelivery = true },
            TestContext.Current.CancellationToken);
        carrier.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await carrier.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Un pedido manual de un producto de 100.000 COP sin impuesto, pendiente de pago.</summary>
    private static async Task<CreateOrderRequest> ManualRequestAsync(HttpClient client, Guid tenantId, ShippingRequest? shipping)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        return new CreateOrderRequest(
            clientId, "Transferencia", null, false, null, null,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
            [new OrderEditItemRequest(productId, 1m)], "PaymentPending", [], shipping);
    }

    /// <summary>Una cotización enviada —todavía editable— de un producto de 100.000 COP con el envío dado.</summary>
    private static async Task<QuotationResponse> QuotationWithShippingAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId, ShippingRequest shipping)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        using var request = new HttpRequestMessage(HttpMethod.Put, QuotationUrl(tenantId, quotation.Id))
        {
            Content = JsonContent.Create(new SaveQuotationRequest(
                quotation.ValidUntil,
                quotation.PaymentMethod,
                quotation.Notes,
                null,
                quotation.BillingAccount is { } billing
                    ? new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency)
                    : null,
                [new QuotationEditItemRequest(productId, 1m)],
                Shipping: shipping)),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{quotation.Version}\"");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, text);
        var saved = JsonSerializer.Deserialize<QuotationResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(saved);
        return saved;
    }

    /// <summary>La cotización de 100.000 COP sin envío, convertida con el pago pendiente.</summary>
    private static async Task<OrderDetailResponse> PendingOrderAsync(HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        using var response = await client.PostAsJsonAsync(
            $"{QuotationUrl(tenantId, quotation.Id)}/order",
            new ConvertQuotationToOrderRequest("PaymentPending", null, []),
            TestContext.Current.CancellationToken);
        return await ReadDetailAsync(response, HttpStatusCode.Created);
    }

    /// <summary>El pedido pendiente con un comprobante igual a su neto: queda pagado.</summary>
    private static async Task<OrderDetailResponse> PaidOrderAsync(HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var pending = await PendingOrderAsync(client, factory, tenantId);
        var proofFileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        using var response = await PutEditsAsync(
            client, tenantId, pending.Order,
            new SaveOrderEditsRequest(
                pending.Items.Select(item => new OrderEditItemRequest(item.ProductId, item.Quantity)).ToArray(),
                new OrderEditProofsRequest([new OrderEditProofAddRequest(proofFileId, pending.Order.NetTotal)], null, null),
                pending.Order.Notes));
        return await ReadDetailAsync(response, HttpStatusCode.OK);
    }

    private static SaveOrderEditsRequest EditsFor(OrderDetailResponse detail, ShippingRequest? shipping) =>
        new(
            detail.Items.Select(item => new OrderEditItemRequest(item.ProductId, item.Quantity)).ToArray(),
            null,
            detail.Order.Notes,
            detail.Order.GlobalScaleFloor,
            detail.Order.IsRetail,
            shipping);

    private static async Task<HttpResponseMessage> PutEditsAsync(
        HttpClient client, Guid tenantId, OrderResponse order, SaveOrderEditsRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, OrderByIdUrl(tenantId, order.Id))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{order.Version}\"");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<OrderDetailResponse> ReadDetailAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {text}");
        var detail = JsonSerializer.Deserialize<OrderDetailResponse>(text, JsonSerializerOptions.Web);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task<OrderDetailResponse> GetDetailAsync(HttpClient client, Guid tenantId, Guid orderId)
    {
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(tenantId, orderId), TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    // Misma lectura que OrderEditsApiTests.ActionOf: el payload del evento de auditoría lleva la acción en `action`.
    private static async Task<string[]> ShippingAuditsAsync(QepApiFactory factory) =>
        (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"))
            .Select(message =>
            {
                using var payload = JsonDocument.Parse(message.PayloadJson);
                return payload.RootElement.GetProperty("action").GetString()!;
            })
            .Where(action => action == "quotation.order.shipping_changed")
            .ToArray();

    private static async Task AssertCodeAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"Expected 422, got {response.StatusCode}: {text}");
        Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }
}
