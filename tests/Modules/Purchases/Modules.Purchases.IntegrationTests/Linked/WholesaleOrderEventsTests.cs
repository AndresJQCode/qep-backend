using System.Text.Json;
using Modules.Purchases.Domain;

namespace Modules.Purchases.IntegrationTests;

/// <summary>
/// Mayorista fase 3: los eventos de vuelta. El mayorista aprueba (tal cual o con cambios) o anula; Compras los consume con su
/// inbox, confirma o cancela la orden y crea en el catálogo propio lo que falte, en la misma transacción.
/// </summary>
public sealed class WholesaleOrderEventsTests(SharedPurchasesHost host) : IClassFixture<SharedPurchasesHost>
{
    private PurchasesApiHarness.QepApiFactory Factory => host.Factory;

    // El payload lleva todas las líneas finales, en camelCase; la que agregó el mayorista sin línea de origen.
    [Fact]
    public async Task ApprovedPayloadCarriesEveryFinalLineInCamelCase()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");
        var orderId = await world.WholesaleOrderIdAsync(sent.Id);
        await world.EditWholesaleOrderAsync(orderId, (world.VariantA, 2m, null), (world.VariantB, 1m, null));

        await world.ApproveAsync(sent.Id);
        var payload = await world.ApprovedPayloadAsync(orderId);

        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal(
            (world.BuyerTenant.TenantId, sent.Id, "COP"),
            (root.GetProperty("buyerTenantId").GetGuid(), root.GetProperty("purchaseOrderId").GetGuid(), root.GetProperty("currency").GetString()));
        var lines = root.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        var original = lines.Single(line => line.GetProperty("variantId").GetGuid() == world.VariantA);
        var added = lines.Single(line => line.GetProperty("variantId").GetGuid() == world.VariantB);
        Assert.Equal(Assert.Single(sent.Lines).Id, original.GetProperty("sourcePurchaseOrderLineId").GetGuid());
        Assert.Equal(JsonValueKind.Null, added.GetProperty("sourcePurchaseOrderLineId").ValueKind);
        Assert.Equal(("JAB-2", LinkedPurchasesHarness.ListPrice, 19m), (added.GetProperty("code").GetString(), added.GetProperty("unitPrice").GetDecimal(), added.GetProperty("taxPercentage").GetDecimal()));
    }

    // Cantidad y precio cambiados, una línea quitada y una agregada: la orden vuelve confirmada con cambios, línea por línea.
    [Fact]
    public async Task ApprovedWithChangesComesBackAsConfirmedWithChanges()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m), (world.VariantB, 1m));
        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");
        var orderId = await world.WholesaleOrderIdAsync(sent.Id);
        await world.EditWholesaleOrderAsync(orderId, (world.VariantA, 3m, 8000m), (world.VariantC, 4m, null));

        await world.ApproveAsync(sent.Id);
        await world.ProcessEventsAsync();
        var confirmed = await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, sent.Id);

        Assert.Equal(("Confirmed", true), (confirmed.Status, confirmed.ConfirmedWithChanges));
        var a = confirmed.Lines.Single(line => line.Code == "ACE-1");
        var b = confirmed.Lines.Single(line => line.Code == "JAB-2");
        var c = confirmed.Lines.Single(line => line.Code == "SAL-3");
        Assert.Equal(("Changed", 3m, PurchaseOrderMath.NetOfVat(8000m, 19m)), (a.LineStatus, a.ConfirmedQuantity!.Value, a.ConfirmedUnitCost!.Value));
        Assert.Equal(("Rejected", 0m), (b.LineStatus, b.ConfirmedQuantity!.Value));
        Assert.Equal(("Added", (decimal?)null, 4m), (c.LineStatus, c.Quantity, c.ConfirmedQuantity!.Value));
        Assert.Equal(new Application.PurchaseOrderChangesDto(1, 1, 1, 2), confirmed.Changes);
        Assert.Equal("El mayorista", confirmed.History[^1].ActorName);
    }

    [Fact]
    public async Task MissingProductsAreCreatedWithCostAndNoPrice()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");

        await world.ApproveAsync(sent.Id);
        await world.ProcessEventsAsync();
        var confirmed = await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, sent.Id);

        var line = Assert.Single(confirmed.Lines);
        Assert.True(line.CreatedInCatalog);
        var variantId = line.VariantId!.Value;
        var prices = await PurchasesApiHarness.ScalarAsync<int>(
            Factory, $"""SELECT count(*)::int AS "Value" FROM catalog.product_prices WHERE variant_id = {variantId}""");
        var cost = await PurchasesApiHarness.ScalarAsync<decimal>(
            Factory, $"""SELECT amount AS "Value" FROM catalog.product_variant_costs WHERE variant_id = {variantId} AND currency = 'COP'""");
        var source = await PurchasesApiHarness.RowsAsync<string>(
            Factory,
            $"""
            SELECT p.external_source AS "Value" FROM catalog.products p
            JOIN catalog.product_variants v ON v.product_id = p.id
            WHERE v.id = {variantId} AND v.tenant_id = {world.BuyerTenant.TenantId} AND v.code = 'ACE-1' AND p.external_source IS NOT NULL
            """);
        Assert.Equal(0, prices);
        Assert.Equal(PurchaseOrderMath.NetOfVat(LinkedPurchasesHarness.ListPrice, 19m), cost);
        Assert.Empty(source);
    }

    // Review Focus 4: un producto que ya tiene ese código se usa y no se toca: misma fila y misma versión.
    [Fact]
    public async Task ExistingProductsStayByteForByte()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var own = await PurchasesApiHarness.CreateVariantAsync(world.Buyer, world.BuyerTenant.TenantId, "ACE-1", "Aceite propio", null);
        var before = await world.ProductRowAsync(own);
        var draft = await world.CreateDraftAsync((world.VariantA, 2m));
        var sent = await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, draft, "send");

        await world.ApproveAsync(sent.Id);
        await world.ProcessEventsAsync();
        var confirmed = await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, sent.Id);

        var line = Assert.Single(confirmed.Lines);
        Assert.Equal((own, false), (line.VariantId!.Value, line.CreatedInCatalog));
        Assert.Equal(before, await world.ProductRowAsync(own));
    }

    // D-21: la anulación del mayorista cancela la orden salvo si ya se recibió.
    [Fact]
    public async Task WholesalerCancelAfterApprovalCancelsUnlessReceived()
    {
        using var world = await LinkedPurchasesHarness.NewWorldAsync(Factory);
        var toCancel = await PurchasesApiHarness.TransitionAsync(
            world.Buyer, world.BuyerTenant.TenantId, await world.CreateDraftAsync((world.VariantA, 2m)), "send");
        var toReceive = await PurchasesApiHarness.TransitionAsync(
            world.Buyer, world.BuyerTenant.TenantId, await world.CreateDraftAsync((world.VariantB, 1m)), "send");
        await world.ApproveAsync(toCancel.Id);
        await world.ApproveAsync(toReceive.Id);
        await world.ProcessEventsAsync();
        var confirmed = await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, toReceive.Id);
        await PurchasesApiHarness.TransitionAsync(world.Buyer, world.BuyerTenant.TenantId, confirmed, "receive");

        await world.CancelWholesaleOrderAsync(await world.WholesaleOrderIdAsync(toCancel.Id));
        await world.CancelWholesaleOrderAsync(await world.WholesaleOrderIdAsync(toReceive.Id));
        await world.ProcessEventsAsync();

        Assert.Equal("Cancelled", (await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, toCancel.Id)).Status);
        Assert.Equal("Received", (await PurchasesApiHarness.GetOrderAsync(world.Buyer, world.BuyerTenant.TenantId, toReceive.Id)).Status);
    }
}
