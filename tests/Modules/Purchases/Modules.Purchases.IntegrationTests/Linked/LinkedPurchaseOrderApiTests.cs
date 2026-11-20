using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Modules.Purchases.Domain;

namespace Modules.Purchases.IntegrationTests.Linked;

/// <summary>
/// Mayorista fase 3: «Enviar» una orden vinculada crea el pedido en el mayorista al precio de lista de ese momento (D-8, D-9),
/// una sola vez aunque se reintente, y cancelarla anula el pedido mientras el mayorista no lo apruebe (D-12).
/// </summary>
public sealed class LinkedPurchaseOrderApiTests(SharedPurchasesHost host) : IClassFixture<SharedPurchasesHost>
{
    private PurchasesApiHarness.QepApiFactory Factory => host.Factory;

    // La lista cambia entre el borrador y el envío: el pedido y la orden quedan con el precio nuevo.
    [Fact]
    public async Task SendingToAWholesalerCreatesTheOrderWithTheRecalculatedPrice()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        Assert.Equal(PurchaseOrderMath.NetOfVat(LinkedPurchasesHarness.ListPrice, 19m), Assert.Single(draft.Lines).UnitCost);
        await world.ChangeDiscountAsync(20m);

        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");

        Assert.Equal(("Sent", "Linked"), (sent.Status, sent.Mode));
        var line = Assert.Single(sent.Lines);
        Assert.Equal(PurchaseOrderMath.NetOfVat(8000m, 19m), line.UnitCost);
        Assert.Equal(new Application.PurchaseOrderListPriceDto(LinkedPurchasesHarness.BasePrice, 20m, 8000m), line.ListPrice);
        using var order = await world.WholesaleOrderAsync(sent.Id);
        var item = order.RootElement.GetProperty("items")[0];
        Assert.Equal((8000m, 2m), (item.GetProperty("unitPrice").GetDecimal(), item.GetProperty("quantity").GetDecimal()));
        Assert.Equal(sent.Linked!.OrderNumber, order.RootElement.GetProperty("order").GetProperty("orderNumber").GetString());
    }

    // Review Focus 1 (D-8): el pedido se creó y la orden no alcanzó a guardarse. Reenviar no crea otro y adopta el que hay.
    [Fact]
    public async Task SendingTwiceCreatesOneWholesaleOrder()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        var first = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");
        await world.ForgetTheSendAsync(first.Id);
        var reverted = await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, first.Id);
        Assert.Equal("Draft", reverted.Status);

        var second = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, reverted, "send");

        Assert.Equal(1, await world.WholesaleOrderCountAsync());
        Assert.Equal(first.Linked!.OrderNumber, second.Linked!.OrderNumber);
        Assert.Equal(first.Lines.Select(line => line.Id), second.Lines.Select(line => line.Id));
    }

    [Fact]
    public async Task CancelBeforeApprovalCancelsBothSides()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");

        var cancelled = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, sent, "cancel");

        Assert.Equal("Cancelled", cancelled.Status);
        using var order = await world.WholesaleOrderAsync(sent.Id);
        Assert.Equal("Cancelled", order.RootElement.GetProperty("order").GetProperty("status").GetString());
        Assert.True(order.RootElement.GetProperty("wholesale").GetProperty("cancelledByBuyer").GetBoolean());
    }

    // Review Focus 2: el mayorista aprobó primero. El 409 deja la orden enviada y el evento de aprobación la confirma.
    [Fact]
    public async Task CancelAfterApprovalIs409AndTheEventStillConfirms()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");
        await world.ApproveAsync(sent.Id);

        var cancel = await PurchasesApiHarness.WriteAsync(
            world.Buyer, HttpMethod.Post, $"{PurchasesApiHarness.OrderUrl(world.BuyerTenant.TenantId, sent.Id)}/cancel", sent.Version);
        await world.ProcessEventsAsync();

        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal(PurchasesErrorCodes.NotCancellable, await PurchasesApiHarness.CodeOfAsync(cancel));
        var confirmed = await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, sent.Id);
        Assert.Equal(("Confirmed", false), (confirmed.Status, confirmed.ConfirmedWithChanges));
    }

    // D-7: el mayorista ve el número de la orden y el nombre del distribuidor.
    [Fact]
    public async Task TheWholesalerSeesThePurchaseOrderNumber()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");

        using var order = await world.WholesaleOrderAsync(sent.Id);

        var root = order.RootElement;
        Assert.Equal(sent.Number, root.GetProperty("order").GetProperty("externalReference").GetString());
        Assert.Equal(sent.Number, root.GetProperty("wholesale").GetProperty("purchaseOrderNumber").GetString());
        Assert.Equal(LinkedPurchasesHarness.BuyerName, root.GetProperty("wholesale").GetProperty("buyerTenant").GetProperty("name").GetString());
        Assert.Equal("Pending", root.GetProperty("wholesale").GetProperty("approval").GetString());
    }

    // Revisión del bloque B, M1: el envío creó el pedido y la orden quedó en borrador. Cancelarla anula ese pedido.
    [Fact]
    public async Task CancellingADraftLeftByAFailedSendCancelsItsWholesaleOrder()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");
        await world.ForgetTheSendAsync(sent.Id);
        var reverted = await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, sent.Id);

        await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, reverted, "cancel");

        using var order = await world.WholesaleOrderAsync(sent.Id);
        Assert.Equal("Cancelled", order.RootElement.GetProperty("order").GetProperty("status").GetString());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] PriceFields = ["basePrice", "discountPercent", "listPrice", "unitCost"];

    // Spec 2026-11-19: con la lista oculta el catálogo no trae ningún monto (los campos van null o ausentes).
    [Fact]
    public async Task CatalogOfHiddenPriceListHasNoPrices()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        await world.SetShowPricesAsync(false);

        var response = await world.Buyer.GetAsync(world.CatalogUrl(), Ct);
        var json = await PurchasesApiHarness.BodyAsync(response);
        TestContext.Current.SendDiagnosticMessage("HIDDEN CATALOG JSON: " + json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.False(root.GetProperty("showPrices").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("priceList").GetProperty("discountPercent").ValueKind);
        Assert.Equal(3, root.GetProperty("items").GetArrayLength());
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            foreach (var field in PriceFields)
            {
                Assert.True(
                    !item.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null,
                    $"{field} must not carry an amount");
            }

            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("code").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("name").GetString()));
            Assert.Equal(19m, item.GetProperty("taxRatePercent").GetDecimal());
        }
    }

    [Fact]
    public async Task CatalogOfVisiblePriceListKeepsPrices()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);

        var response = await world.Buyer.GetAsync(world.CatalogUrl(), Ct);

        using var document = JsonDocument.Parse(await PurchasesApiHarness.BodyAsync(response));
        Assert.True(document.RootElement.GetProperty("showPrices").GetBoolean());
        var item = document.RootElement.GetProperty("items")[0];
        Assert.Equal(
            (LinkedPurchasesHarness.BasePrice, LinkedPurchasesHarness.DiscountPercent, LinkedPurchasesHarness.ListPrice),
            (item.GetProperty("basePrice").GetDecimal(), item.GetProperty("discountPercent").GetDecimal(), item.GetProperty("listPrice").GetDecimal()));
    }

    // La vista previa se enmascara con el flag vivo; el costo se sigue guardando al crear (D-4).
    [Fact]
    public async Task PreviewOfHiddenPriceListHasNoAmounts()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        await world.SetShowPricesAsync(false);
        var previewUrl = $"{PurchasesApiHarness.OrderUrl(world.BuyerTenant.TenantId)}/preview";

        var response = await world.Buyer.PostAsJsonAsync(previewUrl, world.DraftBody((world.VariantA, 2m)), Ct);
        var json = await PurchasesApiHarness.BodyAsync(response);
        TestContext.Current.SendDiagnosticMessage("HIDDEN PREVIEW JSON: " + json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.False(root.GetProperty("showPrices").GetBoolean());
        Assert.True(!root.TryGetProperty("totals", out var totals) || totals.ValueKind == JsonValueKind.Null);
        var line = Assert.Single(root.GetProperty("lines").EnumerateArray().ToList());
        foreach (var field in new[] { "subtotal", "listPrice" })
        {
            Assert.True(!line.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null, field);
        }

        Assert.Equal("ACE-1", line.GetProperty("code").GetString());

        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        Assert.Equal(PurchaseOrderMath.NetOfVat(LinkedPurchasesHarness.ListPrice, 19m), Assert.Single(draft.Lines).UnitCost);
    }

    // El documento propio del mayorista siempre lleva montos: el PDF de su pedido no lee el flag de la lista.
    [Fact]
    public async Task TheWholesalersOwnOrderPdfKeepsAmountsWithTheListHidden()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        await world.SetShowPricesAsync(false);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");
        var orderId = await world.WholesaleOrderIdAsync(sent.Id);
        var orderNumber = await world.WholesaleOrderNumberAsync(orderId);

        var response = await world.Wholesaler.PostAsync(
            $"/api/v1/tenants/{world.WholesalerTenant.TenantId}/orders/{orderId}/pdf", content: null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = Factory.Services.GetRequiredService<FakeQuotationPdfRenderer>().DocumentOf(orderNumber)!;
        var line = Assert.Single(document.Items);
        Assert.Equal(LinkedPurchasesHarness.ListPrice * 2m, document.Total);
        Assert.True(document.Subtotal > 0m);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(LinkedPurchasesHarness.ListPrice, line.UnitPrice);
    }
}
