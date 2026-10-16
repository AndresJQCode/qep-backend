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
