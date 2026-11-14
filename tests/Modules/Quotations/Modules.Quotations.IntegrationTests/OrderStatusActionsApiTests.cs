using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Persistence;
using static Modules.Quotations.IntegrationTests.QuotationsApiHarness;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Las acciones del pedido de spec 2026-11-10 por HTTP: marcar y deshacer pagado (<c>approve</c>), preparar,
/// deshacer el preparado, archivar y desarchivar (<c>manage</c>), facturar con el ajuste del tenant, el
/// archivado automático y el 403 de quitar un comprobante de un pedido pagado por las dos rutas.
/// </summary>
public sealed class OrderStatusActionsApiTests(SharedQuotationsHost sharedHost) : IClassFixture<SharedQuotationsHost>
{
    private const string SettingsRead = "tenancy.settings.read";
    private const string SettingsUpdate = "tenancy.settings.update";

    private static readonly string[] AllPermissions =
    [
        .. ApproverPermissions, OrdersPermissions.OrderInvoice, OrdersPermissions.OrderCancel, SettingsRead, SettingsUpdate,
    ];

    private static string OrderByIdUrl(Guid tenantId, Guid orderId) => $"/api/v1/tenants/{tenantId}/orders/{orderId}";

    private static string ActionUrl(Guid tenantId, Guid orderId, string action) =>
        $"{OrderByIdUrl(tenantId, orderId)}/{action}";

    [Fact]
    public async Task TheFullFlowPaysPreparesInvoicesAndArchives()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, AllPermissions);
        using var _ = client;
        var (order, _) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);
        Assert.Equal("PendingVerification", order.PaymentStatus);

        var paid = await PostActionAsync(client, tenantId, order.Id, "mark-paid");
        Assert.Equal(("Open", "FullPaymentReceived"), (paid.Status, paid.PaymentStatus));

        var prepared = await PostActionAsync(client, tenantId, order.Id, "prepare");
        Assert.Equal("Prepared", prepared.FulfillmentStatus);

        var invoiced = await PostActionAsync(client, tenantId, order.Id, "invoice");
        Assert.NotNull(invoiced.InvoicedAt);

        var archived = await PostActionAsync(client, tenantId, order.Id, "archive");
        Assert.Equal("Archived", archived.Status);

        // Anti-meta §2: archivar no quita la marca de facturado.
        var detail = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal("Archived", detail.Order.Status);
        Assert.NotNull(detail.Order.InvoicedAt);
    }

    [Fact]
    public async Task ApproveIsGone()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, AllPermissions);
        using var _ = client;
        var (order, _) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);

        var response = await client.PostAsync(
            ActionUrl(tenantId, order.Id, "approve"), content: null, TestContext.Current.CancellationToken);

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
    }

    [Fact]
    public async Task AutoArchiveHappensWhenTheSecondConditionArrives()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, AllPermissions);
        using var _ = client;
        (await client.PutAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/orders-settings",
            new { requirePaymentProofToMarkPaid = false, requireFullPaymentToInvoice = true, autoArchive = true },
            TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        var (order, _) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);

        var prepared = await PostActionAsync(client, tenantId, order.Id, "prepare");
        Assert.Equal("Open", prepared.Status);

        var paid = await PostActionAsync(client, tenantId, order.Id, "mark-paid");
        Assert.Equal("Archived", paid.Status);

        var detail = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Contains(
            detail.History,
            entry => entry.EventType == "Archived"
                && entry.Details == "Archivado automáticamente: quedó pagado y preparado.");
    }

    // Review Focus 2: ningún camino con sólo `manage` quita un comprobante de un pedido pagado.
    [Fact]
    public async Task AManageOnlyMemberCannotRemoveAProofFromAPaidOrderByEitherRoute()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, ownerUserId, client) = await RegisterTenantAsync(factory, AllPermissions);
        using var _ = client;
        var (order, variantId) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);
        var paid = await PostActionAsync(client, tenantId, order.Id, "mark-paid");
        var proofId = Assert.Single(paid.PaymentProofs).Id;
        using var manager = CreateClient(factory, ownerUserId.ToString(), tenantId.ToString(), ManagerPermissions);

        var delete = await manager.DeleteAsync(
            $"{OrderByIdUrl(tenantId, order.Id)}/proofs/{proofId}", TestContext.Current.CancellationToken);
        using var put = new HttpRequestMessage(HttpMethod.Put, OrderByIdUrl(tenantId, order.Id))
        {
            Content = JsonContent.Create(new SaveOrderEditsRequest(
                [new OrderEditItemRequest(variantId, 1m)],
                new OrderEditProofsRequest(null, null, [proofId]),
                null)),
        };
        put.Headers.TryAddWithoutValidation("If-Match", $"\"{paid.Version}\"");
        var edit = await manager.SendAsync(put, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
        var detail = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Single(detail.Order.PaymentProofs);
        Assert.Equal("FullPaymentReceived", detail.Order.PaymentStatus);
    }

    [Fact]
    public async Task APreparedOrderCannotBeEditedNorCancelledByTheConsole()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, AllPermissions);
        using var _ = client;
        var (order, variantId) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);
        var prepared = await PostActionAsync(client, tenantId, order.Id, "prepare");

        using var put = new HttpRequestMessage(HttpMethod.Put, OrderByIdUrl(tenantId, order.Id))
        {
            Content = JsonContent.Create(new SaveOrderEditsRequest(
                [new OrderEditItemRequest(variantId, 2m)], null, null)),
        };
        put.Headers.TryAddWithoutValidation("If-Match", $"\"{prepared.Version}\"");
        var edit = await client.SendAsync(put, TestContext.Current.CancellationToken);
        var cancel = await client.PostAsJsonAsync(
            ActionUrl(tenantId, order.Id, "cancel"), new CancelOrderRequest("Ya no lo quiere"),
            TestContext.Current.CancellationToken);

        await AssertDomainRejectionAsync(edit, "order.order.not_editable");
        await AssertDomainRejectionAsync(cancel, "order.order.already_prepared");
    }

    [Fact]
    public async Task EveryActionLeavesHistoryAndAudit()
    {
        var factory = await sharedHost.StartTestAsync();
        var (tenantId, _, client) = await RegisterTenantAsync(factory, AllPermissions);
        using var _ = client;
        var (order, _) = await CreateOrderCoveredByAProofAsync(client, factory, tenantId);

        foreach (var action in new[] { "mark-paid", "unmark-paid", "prepare", "unprepare", "archive", "unarchive" })
        {
            await PostActionAsync(client, tenantId, order.Id, action);
        }

        var detail = await GetDetailAsync(client, tenantId, order.Id);
        Assert.Equal(
            [
                ("PaidMarked", "Marcado como pagado."),
                ("PaidUnmarked", "Se deshizo el pagado."),
                ("Prepared", "Marcado como preparado."),
                ("PreparationUndone", "Se deshizo el preparado."),
                ("Archived", "Archivado."),
                ("Unarchived", "Desarchivado."),
            ],
            detail.History
                .Where(entry => entry.EventType is "PaidMarked" or "PaidUnmarked" or "Prepared"
                    or "PreparationUndone" or "Archived" or "Unarchived")
                .OrderBy(entry => entry.EventAt)
                .Select(entry => (entry.EventType, entry.Details!))
                .ToArray());
        string[] expected =
        [
            "quotation.order.paid_marked", "quotation.order.paid_unmarked", "quotation.order.prepared",
            "quotation.order.preparation_undone", "quotation.order.archived", "quotation.order.unarchived",
        ];
        Assert.Equal(
            expected,
            (await OutboxMessagesAsync(factory, "platform.audit.recorded.v1"))
                .Where(message => EntityIdOf(message) == order.Id.ToString())
                .Select(ActionOf)
                .Where(expected.Contains)
                .ToArray());
        Assert.Equal("Open", detail.Order.Status);
        Assert.Equal("NotPrepared", detail.Order.FulfillmentStatus);
    }

    /// <summary>Una cotización enviada con un producto de 100.000 COP sin impuesto, convertida con un
    /// comprobante de 100.000: el pedido queda «Por verificar», listo para marcarlo pagado.</summary>
    private static async Task<(OrderResponse Order, Guid VariantId)> CreateOrderCoveredByAProofAsync(
        HttpClient client, QepApiFactory factory, Guid tenantId)
    {
        var clientId = await CreateActiveCustomerAsync(client, tenantId);
        var variantId = await CreateProductWithScalesAsync(client, tenantId, baseCop: 100_000m);
        var quotation = await CreateSentQuotationAsync(client, factory, tenantId, clientId, variantId);
        var fileId = await CreateAvailablePaymentProofFileAsync(client, factory, tenantId);
        var response = await client.PostAsJsonAsync(
            $"{QuotationsUrl(tenantId)}/{quotation.Id}/order",
            new ConvertQuotationToOrderRequest(null, [new OrderPaymentProofRequest(fileId, quotation.NetTotal)]),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadConvertedOrderAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return (order, variantId);
    }

    private static async Task<OrderResponse> PostActionAsync(HttpClient client, Guid tenantId, Guid orderId, string action)
    {
        var response = await client.PostAsync(
            ActionUrl(tenantId, orderId, action), content: null, TestContext.Current.CancellationToken);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{action}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(order);
        return order;
    }

    private static async Task<OrderDetailResponse> GetDetailAsync(HttpClient client, Guid tenantId, Guid orderId)
    {
        var detail = await client.GetFromJsonAsync<OrderDetailResponse>(
            OrderByIdUrl(tenantId, orderId), TestContext.Current.CancellationToken);
        Assert.NotNull(detail);
        return detail;
    }

    private static async Task AssertDomainRejectionAsync(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
    }

    private static string ActionOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("action").GetString()!;
    }

    private static string EntityIdOf(QuotationsOutboxMessage message)
    {
        using var payload = JsonDocument.Parse(message.PayloadJson);
        return payload.RootElement.GetProperty("resourceId").GetString()!;
    }
}
