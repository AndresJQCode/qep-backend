using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using Npgsql;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>Anular, facturar y revertir la facturación de un pedido.</summary>
public sealed class OrderCancelAndInvoiceApiTests(SharedQuotationsHost sharedHost) : OrderApiTests, IClassFixture<SharedQuotationsHost>
{
    // Spec 2026-09-16: anular desde Pending deja quién, cuándo y por qué, lo persiste, no toca la
    // cotización (decisión 3) y lo audita. Los nombres de los campos se leen del JSON crudo: son el
    // contrato que consume el frontend.
    [Fact]
    public async Task CancelAPendingOrderReturnsItCancelledWithWhoWhenAndWhy()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var converted = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var response = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, converted.Id),
            new CancelOrderRequest("  El cliente desistió  "),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            var root = json.RootElement;
            Assert.Equal("Cancelled", root.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.String, root.GetProperty("cancelledAt").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("cancelledBy").ValueKind);
            Assert.Equal("El cliente desistió", root.GetProperty("cancellationReason").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("paidMarkedAt").ValueKind);
            Assert.False(root.TryGetProperty("approvedAt", out var ignoredApprovedAt));
        }

        var order = JsonSerializer.Deserialize<OrderResponse>(body, JsonSerializerOptions.Web);
        Assert.NotNull(order);
        Assert.Equal(converted.Id, order.Id);
        Assert.NotNull(order.CancelledBy);

        var fetched = await GetOrderByQuotationAsync(client, tenantId, quotation.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Cancelled", fetched.Status);
        Assert.Equal(order.CancelledAt, fetched.CancelledAt);
        Assert.Equal(order.CancelledBy, fetched.CancelledBy);
        Assert.Equal("El cliente desistió", fetched.CancellationReason);

        var fetchedQuotation = await client.GetFromJsonAsync<QuotationResponse>(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}", TestContext.Current.CancellationToken);
        Assert.NotNull(fetchedQuotation);
        Assert.Equal("Converted", fetchedQuotation.Status);

        var auditMessages = await OutboxMessagesAsync(factory, "platform.audit.recorded.v1");
        var cancelled = Assert.Single(
            auditMessages, message => ActionOf(message) == "quotation.order.cancelled");
        Assert.Equal(order.Id.ToString(), EntityIdOf(cancelled));
    }

    // Decisiones 1 y 2: un pagado también se anula, y conserva quién lo marcó como pagado y cuándo.
    [Fact]
    public async Task CancelAPaidOrderKeepsThePaidMark()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. CancellerPermissions, OrdersPermissions.OrderApprove]);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));
        var approved = await ReadOrderAsync(await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/mark-paid",
            null,
            TestContext.Current.CancellationToken));

        var cancelled = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, created.Id),
            new CancelOrderRequest("Aprobado por error"),
            TestContext.Current.CancellationToken));

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.NotNull(cancelled.PaidMarkedAt);
        Assert.Equal(approved.PaidMarkedAt, cancelled.PaidMarkedAt);
        Assert.Equal(approved.PaidMarkedBy, cancelled.PaidMarkedBy);
        Assert.Equal("Aprobado por error", cancelled.CancellationReason);
    }

    // Decisión 5: gestionar pedidos no alcanza para anularlos.
    [Fact]
    public async Task CancelWithOnlyTheManagePermissionIsForbidden()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, ManagerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));

        var response = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, created.Id),
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var order = await GetOrderByQuotationAsync(client, tenantId, quotation.Id);
        Assert.NotNull(order);
        Assert.Equal("Open", order.Status);
    }

    // Un id de pedido que no existe en este tenant: mismo 404 que GET /orders/{orderId}, porque
    // la acción ya no pasa por la cotización.
    [Fact]
    public async Task CancelAnUnknownOrderIsNotFound()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;

        var response = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, Guid.CreateVersion7()),
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("order.order.not_found", body, StringComparison.Ordinal);
    }

    // Los tres 422 son códigos de dominio, no validation.failed (hallazgo 3): el frontend los
    // mapea por `code`. Un solo pedido para los cuatro requests: los rechazados no lo modifican.
    [Fact]
    public async Task CancelRejectsAMissingOrTooLongReasonAndASecondCancellation()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, CancellerPermissions);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));
        var url = OrderCancelUrl(tenantId, created.Id);

        var withoutReason = await client.PostAsJsonAsync(
            url, new { }, TestContext.Current.CancellationToken);
        var blankReason = await client.PostAsJsonAsync(
            url, new CancelOrderRequest("   "), TestContext.Current.CancellationToken);
        var tooLong = await client.PostAsJsonAsync(
            url, new CancelOrderRequest(new string('a', 501)), TestContext.Current.CancellationToken);
        (await client.PostAsJsonAsync(
            url, new CancelOrderRequest("Primera vez"), TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var twice = await client.PostAsJsonAsync(
            url, new CancelOrderRequest("Segunda vez"), TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(withoutReason, "order.order.cancellation_reason_required");
        await AssertDomainRejectionAsync(blankReason, "order.order.cancellation_reason_required");
        await AssertDomainRejectionAsync(tooLong, "order.order.cancellation_reason_too_long");
        await AssertDomainRejectionAsync(twice, "order.order.already_cancelled");
        var order = await GetOrderByQuotationAsync(client, tenantId, quotation.Id);
        Assert.NotNull(order);
        Assert.Equal("Primera vez", order.CancellationReason);
    }

    // Spec 2026-10-05 y 2026-11-10 §3.4: facturar un pagado deja quién y cuándo, conserva la marca
    // de pagado, no cambia el estado (facturado es una marca) y queda auditado. Los nombres de los
    // campos se leen del JSON crudo: son el contrato que consume el frontend.
    [Fact]
    public async Task InvoiceAPaidOrderReturnsItInvoicedWithWhoAndWhen()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            var root = json.RootElement;
            Assert.Equal("Open", root.GetProperty("status").GetString());
            Assert.True(root.GetProperty("invoiced").GetBoolean());
            Assert.Equal(JsonValueKind.String, root.GetProperty("invoicedAt").ValueKind);
            Assert.Equal(JsonValueKind.String, root.GetProperty("invoicedBy").ValueKind);
            Assert.Equal("FullPaymentReceived", root.GetProperty("paymentStatus").GetString());
        }

        var order = JsonSerializer.Deserialize<OrderResponse>(body, JsonSerializerOptions.Web);
        Assert.NotNull(order);
        Assert.Equal(approved.Id, order.Id);
        Assert.Equal(approved.PaidMarkedAt, order.PaidMarkedAt);
        Assert.Equal(approved.PaidMarkedBy, order.PaidMarkedBy);
        Assert.Equal(approved.Version + 1, order.Version);

        var fetched = await GetOrderByQuotationAsync(client, tenantId, order.QuotationId!.Value);
        Assert.NotNull(fetched);
        Assert.Equal(("Open", true), (fetched.Status, fetched.Invoiced));
        Assert.Equal(order.InvoicedAt, fetched.InvoicedAt);
        Assert.Equal(order.InvoicedBy, fetched.InvoicedBy);

        var invoiced = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => ActionOf(message) == "quotation.order.invoiced");
        Assert.Equal(order.Id.ToString(), EntityIdOf(invoiced));
    }

    // Decisión 4: revertir limpia las marcas de facturado y conserva la de pagado. Ya sin facturar,
    // el pedido se puede anular (Review Focus 3).
    [Fact]
    public async Task InvoicingRevertedLeavesTheOrderPaidAndCancellable()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. InvoicerPermissions, OrdersPermissions.OrderCancel]);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);
        (await client.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        var response = await client.PostAsync(
            OrderUninvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using (var json = JsonDocument.Parse(body))
        {
            var root = json.RootElement;
            Assert.Equal("Open", root.GetProperty("status").GetString());
            Assert.False(root.GetProperty("invoiced").GetBoolean());
            Assert.Equal("FullPaymentReceived", root.GetProperty("paymentStatus").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("invoicedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, root.GetProperty("invoicedBy").ValueKind);
        }

        var reverted = JsonSerializer.Deserialize<OrderResponse>(body, JsonSerializerOptions.Web);
        Assert.NotNull(reverted);
        Assert.Equal(approved.PaidMarkedAt, reverted.PaidMarkedAt);
        Assert.Equal(approved.PaidMarkedBy, reverted.PaidMarkedBy);

        var revertedAudit = Assert.Single(
            await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"),
            message => ActionOf(message) == "quotation.order.invoice_reverted");
        Assert.Equal(approved.Id.ToString(), EntityIdOf(revertedAudit));

        var cancelled = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, approved.Id),
            new CancelOrderRequest("Facturado por error"),
            TestContext.Current.CancellationToken));
        Assert.Equal("Cancelled", cancelled.Status);
    }

    // Decisión 5: marcar pagado no alcanza para facturar ni para revertir. Mismo usuario, otro juego de
    // permisos: el stub de desarrollo los toma del header.
    [Fact]
    public async Task InvoiceAndRevertWithOnlyTheApprovePermissionAreForbidden()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var approved = await CreateApprovedOrderAsync(client, factory, tenantId);
        using var approver = CreateClient(
            factory, ownerUserId.ToString(), tenantId.ToString(), ApproverPermissions);

        var invoice = await approver.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, invoice.StatusCode);

        (await client.PostAsync(
            OrderInvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var revert = await approver.PostAsync(
            OrderUninvoiceUrl(tenantId, approved.Id),
            content: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, revert.StatusCode);
        var order = await GetOrderByQuotationAsync(client, tenantId, approved.QuotationId!.Value);
        Assert.NotNull(order);
        Assert.True(order.Invoiced);
    }

    // Un id de pedido que no existe en este tenant: mismo 404 que GET /orders/{orderId}.
    [Fact]
    public async Task InvoiceAndRevertAnUnknownOrderAreNotFound()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, InvoicerPermissions);
        using var _ = client;
        var unknown = Guid.CreateVersion7();

        var invoice = await client.PostAsync(
            OrderInvoiceUrl(tenantId, unknown), content: null, TestContext.Current.CancellationToken);
        var revert = await client.PostAsync(
            OrderUninvoiceUrl(tenantId, unknown), content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, invoice.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, revert.StatusCode);
        Assert.Contains(
            "order.order.not_found",
            await invoice.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
        Assert.Contains(
            "order.order.not_found",
            await revert.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    // Cada 422 con su código de dominio (sin `errors`): el frontend los mapea por `code`. Un solo
    // pedido recorre los estados; los rechazos no lo modifican. Anular un facturado responde
    // already_invoiced aunque el motivo venga en blanco (Review Focus 2); deshacer el pagado de un
    // facturado también, y agregar productos choca con la guarda de edición (spec 2026-11-10 §3.4).
    [Fact]
    public async Task InvoiceAndRevertRejectTheWrongStateWithTheirDomainCodes()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(
            factory, [.. InvoicerPermissions, OrdersPermissions.OrderCancel]);
        using var _ = client;
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var productId = await CreateProductWithScalesAsync(client, tenantId);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, productId);
        var created = await ReadOrderAsync(await client.PostAsJsonAsync(
            OrderUrl(tenantId, quotation.Id),
            new ConvertQuotationToOrderRequest(null, []),
            TestContext.Current.CancellationToken));
        var markPaidUrl = $"{OrderByIdUrl(tenantId, created.Id)}/mark-paid";

        var invoicePending = await client.PostAsync(
            OrderInvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken);
        var revertPending = await client.PostAsync(
            OrderUninvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken);
        (await client.PostAsync(markPaidUrl, content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var revertApproved = await client.PostAsync(
            OrderUninvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken);
        (await client.PostAsync(
            OrderInvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken))
            .EnsureSuccessStatusCode();
        var invoiceTwice = await client.PostAsync(
            OrderInvoiceUrl(tenantId, created.Id), content: null, TestContext.Current.CancellationToken);
        var cancelInvoiced = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, created.Id),
            new CancelOrderRequest("El cliente desistió"),
            TestContext.Current.CancellationToken);
        var cancelInvoicedWithBlankReason = await client.PostAsJsonAsync(
            OrderCancelUrl(tenantId, created.Id),
            new CancelOrderRequest("   "),
            TestContext.Current.CancellationToken);
        var unmarkPaidInvoiced = await client.PostAsync(
            $"{OrderByIdUrl(tenantId, created.Id)}/unmark-paid", content: null, TestContext.Current.CancellationToken);
        var secondProductId = await CreateProductWithScalesAsync(client, tenantId);
        var addItemsToInvoiced = await client.PostAsJsonAsync(
            OrderItemsUrl(tenantId, created.Id),
            new AddOrderItemsRequest([new OrderItemAdditionRequest(secondProductId, 1m)]),
            TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(invoicePending, "order.order.not_paid");
        await AssertDomainRejectionAsync(revertPending, "order.order.not_invoiced");
        await AssertDomainRejectionAsync(revertApproved, "order.order.not_invoiced");
        await AssertDomainRejectionAsync(invoiceTwice, "order.order.already_invoiced");
        await AssertDomainRejectionAsync(cancelInvoiced, "order.order.already_invoiced");
        await AssertDomainRejectionAsync(cancelInvoicedWithBlankReason, "order.order.already_invoiced");
        await AssertDomainRejectionAsync(unmarkPaidInvoiced, "order.order.already_invoiced");
        await AssertDomainRejectionAsync(addItemsToInvoiced, "order.order.not_editable");
        var order = await GetOrderByQuotationAsync(client, tenantId, quotation.Id);
        Assert.NotNull(order);
        Assert.Equal(("Open", true, "FullPaymentReceived"), (order.Status, order.Invoiced, order.PaymentStatus));
        Assert.Null(order.CancellationReason);
    }
}
