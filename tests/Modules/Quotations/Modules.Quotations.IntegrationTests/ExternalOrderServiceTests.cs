using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Spec 2026-10-15 §5.4: el puerto con el que una integración (Shopify) crea pedidos sin sesión HTTP.
/// Las líneas llegan preciadas y el cliente puede estar incompleto; el estado de pago que avisa la plataforma
/// sólo manda mientras el pedido no tenga comprobantes propios.</summary>
public sealed class ExternalOrderServiceTests
{
    private static string OrdersUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/orders";

    [Fact]
    public async Task CreatesAnOrderForAnIncompleteCustomerAndPublishesOrderCreated()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var incompleteId = await CreateActiveCustomerAsync(client, tenantId);
        await IncompleteCustomerQuotationApiTests.MarkIncompleteAsync(database.GetConnectionString(), incompleteId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await OwnerMembershipIdAsync(factory, tenantId, ownerUserId);

        ExternalOrderResult result;
        using (var scope = factory.Services.CreateScope())
        {
            var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
            result = await writer.CreateAsync(
                new ExternalOrderDraft(
                    tenantId, OrderSource.Shopify, "#1001", incompleteId, advisorId, "shopify_payments",
                    "Shopify #1001", null,
                    new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                    [new ExternalOrderLine(productId, 2m, 11_900m, 0m, 19)],
                    OrderPaymentStatus.FullPaymentReceived,
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
        }

        Assert.StartsWith("PED-", result.OrderNumber, StringComparison.Ordinal);
        Assert.Equal(23_800m, result.Total);
        Assert.Equal("COP", result.Currency);
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{result.OrderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(
            ("Shopify", "#1001", "FullPaymentReceived", "Pending"),
            (detail.Order.Source, detail.Order.ExternalReference, detail.Order.PaymentStatus, detail.Order.Status));
        Assert.Equal(incompleteId, detail.Order.ClientId);
        Assert.Equal(advisorId, detail.Order.AdvisorId);
        Assert.Equal("Created", Assert.Single(detail.History).EventType);
        var created = Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        using var payload = JsonDocument.Parse(created.PayloadJson);
        Assert.Equal("Shopify", payload.RootElement.GetProperty("source").GetString());
        // La auditoría la firma la integración, no una persona.
        var audit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => message.PayloadJson.Contains("quotation.order.created", StringComparison.Ordinal));
        using var auditPayload = JsonDocument.Parse(audit.PayloadJson);
        Assert.Equal("Integration", auditPayload.RootElement.GetProperty("actorType").GetString());
    }

    [Fact]
    public async Task SyncPaymentStatusChangesAnOrderWithoutProofsAndIsRejectedWithThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await OwnerMembershipIdAsync(factory, tenantId, ownerUserId);
        // Con un comprobante: el estado lo gobiernan los comprobantes.
        var withProofNumber = await OrderApiTests.ConvertOneAsync(client, factory, tenantId, clientId, productId);
        var page = await client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(tenantId)}?orderNumber={withProofNumber}", TestContext.Current.CancellationToken);
        var withProofId = Assert.Single(page!.Items).Id;

        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        var external = await writer.CreateAsync(
            new ExternalOrderDraft(
                tenantId, OrderSource.Shopify, "#1002", clientId, advisorId, null, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new ExternalOrderLine(productId, 1m, 11_900m, 0m, 19)],
                OrderPaymentStatus.PaymentPending,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await writer.SyncPaymentStatusAsync(
            tenantId, external.OrderId, OrderPaymentStatus.FullPaymentReceived, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            writer.SyncPaymentStatusAsync(
                tenantId, withProofId, OrderPaymentStatus.PaymentPending, TestContext.Current.CancellationToken));

        Assert.Equal("order.order.payment_has_proofs", error.Code);
        var synced = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{external.OrderId}", TestContext.Current.CancellationToken);
        Assert.Equal("FullPaymentReceived", synced!.Order.PaymentStatus);
        var untouched = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{withProofId}", TestContext.Current.CancellationToken);
        Assert.Equal("FullPaymentReceived", untouched!.Order.PaymentStatus);
    }

    [Fact]
    public async Task CancelLeavesTheOrderCancelledWithItsHistoryAndEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await OwnerMembershipIdAsync(factory, tenantId, ownerUserId);

        using var scope = factory.Services.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        var external = await writer.CreateAsync(
            new ExternalOrderDraft(
                tenantId, OrderSource.Shopify, "#1003", clientId, advisorId, null, null, null,
                new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency),
                [new ExternalOrderLine(productId, 1m, 11_900m, 0m, 19)],
                OrderPaymentStatus.PaymentPending,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await writer.CancelAsync(
            tenantId, external.OrderId, advisorId, "Cancelado en Shopify", TestContext.Current.CancellationToken);

        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(tenantId)}/{external.OrderId}", TestContext.Current.CancellationToken);
        Assert.Equal(("Cancelled", "Cancelado en Shopify"), (detail!.Order.Status, detail.Order.CancellationReason));
        Assert.Contains(detail.History, entry => entry.EventType == "Cancelled");
        Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.cancelled.v1"));
    }

    // Spec de Shopify §6.2 y flete §10: el envío que cobró la tienda entra con el pedido y suma al total.
    [Fact]
    public async Task CreateAsyncWithShippingPersistsShippingAndTotals()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;
        var shipping = new ShippingDetails(null, null, FreightChargeMode.Charged, 15_000m, 19, false);

        var result = await CreateAsync(factory, Draft(setup, "#2001", shipping: shipping));

        Assert.False(result.AlreadyExisted);
        Assert.Equal(23_800m + 15_000m, result.Total);
        var detail = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(setup.TenantId)}/{result.OrderId}", TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        Assert.Equal(
            ("Charged", 15_000m, 19, false),
            (detail.Order.Shipping.ChargeMode, detail.Order.Shipping.FreightAmount,
                detail.Order.Shipping.FreightTaxPercentage, detail.Order.Shipping.CashOnDelivery));
        Assert.Equal((12_605.04m, 2_394.96m), (detail.Order.FreightSubtotal, detail.Order.FreightTaxAmount));
        Assert.Equal(38_800m, detail.Order.Total);
    }

    // D-S23: el webhook y la conciliación, o un reintento tras una caída del worker, mandan el mismo pedido dos
    // veces. La segunda llamada devuelve el que ya existe y no escribe nada: ni otro evento ni otro historial.
    [Fact]
    public async Task CreateAsyncWithSameExternalKeyReturnsExistingOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;

        var first = await CreateAsync(factory, Draft(setup, "#2002", externalKey: "shop-1:order-2002"));
        var second = await CreateAsync(factory, Draft(setup, "#2002", externalKey: " shop-1:order-2002 "));

        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
        Assert.Equal(
            (first.OrderId, first.OrderNumber, first.Total, first.NetTotal, first.Currency),
            (second.OrderId, second.OrderNumber, second.Total, second.NetTotal, second.Currency));
        Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        var detail = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(setup.TenantId)}/{first.OrderId}", TestContext.Current.CancellationToken);
        Assert.Single(detail!.History);
        Assert.Equal(1, await CountShopifyOrdersAsync(setup));
    }

    // La carrera que la búsqueda previa no cierra: las dos llamadas buscan antes de que alguna guarde. La cierra
    // el índice IX_orders_tenant_external_key, y la que pierde devuelve el pedido de la que ganó.
    [Fact]
    public async Task CreateAsyncConcurrentSameExternalKeyCreatesOneOrder()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;
        var draft = Draft(setup, "#2003", externalKey: "shop-1:order-2003");

        var results = await Task.WhenAll(CreateAsync(factory, draft), CreateAsync(factory, draft));

        Assert.Equal(results[0].OrderId, results[1].OrderId);
        Assert.Single(results, result => result.AlreadyExisted);
        Assert.Single(await OutboxMessagesAsync(factory, "quotations.order.created.v1"));
        Assert.Equal(1, await CountShopifyOrdersAsync(setup));
    }

    // §6.2.5: la pantalla ofrece «Reconocer este nombre» sin leer el espejo de Shopify.
    [Fact]
    public async Task GetOrderExposesExternalShippingTitle()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var setup = await ArrangeAsync(factory);
        using var _ = setup.Client;

        var withTitle = await CreateAsync(factory, Draft(setup, "#2004", externalShippingTitle: "Envío exprés"));
        var withoutTitle = await CreateAsync(factory, Draft(setup, "#2005"));

        var detail = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(setup.TenantId)}/{withTitle.OrderId}", TestContext.Current.CancellationToken);
        Assert.Equal("Envío exprés", detail!.Order.Shipping.ExternalShippingTitle);
        var plain = await setup.Client.GetFromJsonAsync<OrderDetailResponse>(
            $"{OrdersUrl(setup.TenantId)}/{withoutTitle.OrderId}", TestContext.Current.CancellationToken);
        Assert.Null(plain!.Order.Shipping.ExternalShippingTitle);
    }

    private sealed record ExternalSetup(
        Guid TenantId, HttpClient Client, Guid ClientId, Guid ProductId, Guid AdvisorId,
        QuotationBillingAccountRequest Billing);

    private static async Task<ExternalSetup> ArrangeAsync(QepApiFactory factory)
    {
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var billing = await CreateCompanyWithBankAccountAsync(client, tenantId);
        var advisorId = await OwnerMembershipIdAsync(factory, tenantId, ownerUserId);
        return new ExternalSetup(
            tenantId, client, clientId, productId, advisorId,
            new QuotationBillingAccountRequest(billing.CompanyId, billing.BankName, billing.AccountNumber, billing.Currency));
    }

    private static ExternalOrderDraft Draft(
        ExternalSetup setup,
        string reference,
        ShippingDetails? shipping = null,
        string? externalKey = null,
        string? externalShippingTitle = null) =>
        new(
            setup.TenantId, OrderSource.Shopify, reference, setup.ClientId, setup.AdvisorId, null, null, null,
            setup.Billing, [new ExternalOrderLine(setup.ProductId, 2m, 11_900m, 0m, 19)],
            OrderPaymentStatus.PaymentPending, DateTimeOffset.UtcNow, shipping, externalKey, externalShippingTitle);

    // Un scope por llamada, como el worker: cada una con su DbContext.
    private static async Task<ExternalOrderResult> CreateAsync(QepApiFactory factory, ExternalOrderDraft draft)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IExternalOrderWriter>();
        return await writer.CreateAsync(draft, TestContext.Current.CancellationToken);
    }

    private static async Task<int> CountShopifyOrdersAsync(ExternalSetup setup)
    {
        var page = await setup.Client.GetFromJsonAsync<OrdersPageResponse>(
            $"{OrdersUrl(setup.TenantId)}?source=Shopify", TestContext.Current.CancellationToken);
        return page!.Total;
    }

    // La membresía activa del owner, por el puerto de Tenancy: el roster HTTP exige un permiso que
    // ManagerPermissions no concede, y lo que se prueba acá no es ese endpoint.
    private static async Task<Guid> OwnerMembershipIdAsync(QepApiFactory factory, Guid tenantId, Guid ownerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var directory = scope.ServiceProvider.GetRequiredService<IMembershipDirectory>();
        var membershipId = await directory.FindActiveMembershipIdAsync(ownerUserId, tenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(membershipId);
        return membershipId.Value;
    }
}
