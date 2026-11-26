using Modules.Purchases.Infrastructure.Messaging;

namespace Modules.Purchases.UnitTests;

/// <summary>
/// Mayorista fase 3 (Task 7): los eventos de vuelta del mayorista se leen por nombre (camelCase), como los escribe
/// <c>QuotationLifecycleEventPublisher</c>. Un payload roto es determinista: se lee como inválido y el procesador lo marca sin
/// efecto.
/// </summary>
public sealed class WholesaleOrderEventPayloadsTests
{
    private static readonly Guid Wholesaler = Guid.Parse("0196f1a0-0000-7000-8000-000000000001");
    private static readonly Guid Order = Guid.Parse("0196f1a0-0000-7000-8000-000000000002");
    private static readonly Guid Buyer = Guid.Parse("0196f1a0-0000-7000-8000-000000000003");
    private static readonly Guid PurchaseOrder = Guid.Parse("0196f1a0-0000-7000-8000-000000000004");
    private static readonly Guid SourceLine = Guid.Parse("0196f1a0-0000-7000-8000-000000000005");
    private static readonly Guid VariantA = Guid.Parse("0196f1a0-0000-7000-8000-000000000006");
    private static readonly Guid VariantB = Guid.Parse("0196f1a0-0000-7000-8000-000000000007");

    [Fact]
    public void ReadsAnApprovalWithANullSourceLineId()
    {
        var json = $$"""
            {"tenantId":"{{Wholesaler}}","orderId":"{{Order}}","orderNumber":"PED-0042","buyerTenantId":"{{Buyer}}",
             "purchaseOrderId":"{{PurchaseOrder}}","currency":"COP","approvedAt":"2026-11-18T10:00:00+00:00",
             "lines":[
               {"sourcePurchaseOrderLineId":"{{SourceLine}}","variantId":"{{VariantA}}","code":"ACE-1","name":"Aceite",
                "quantity":3,"unitPrice":11900,"taxPercentage":19},
               {"sourcePurchaseOrderLineId":null,"variantId":"{{VariantB}}","code":"JAB-2","name":"Jabón",
                "quantity":1.5,"unitPrice":5000,"taxPercentage":0}]}
            """;

        Assert.True(WholesaleOrderEventPayloads.TryReadApproved(json, out var approval));

        Assert.Equal((Wholesaler, Order, Buyer, PurchaseOrder, "COP"), (approval.WholesalerTenantId, approval.OrderId, approval.BuyerTenantId, approval.PurchaseOrderId, approval.Currency));
        Assert.Equal(
            [
                new Application.WholesaleApprovedLine(SourceLine, VariantA, "ACE-1", "Aceite", 3m, 11900m, 19m),
                new Application.WholesaleApprovedLine(null, VariantB, "JAB-2", "Jabón", 1.5m, 5000m, 0m),
            ],
            approval.Lines);
    }

    [Fact]
    public void ReadsACancellation()
    {
        var json = $$"""
            {"tenantId":"{{Wholesaler}}","orderId":"{{Order}}","buyerTenantId":"{{Buyer}}","purchaseOrderId":"{{PurchaseOrder}}",
             "cancelledAt":"2026-11-18T10:00:00+00:00","byBuyer":false}
            """;

        Assert.True(WholesaleOrderEventPayloads.TryReadCancelled(json, out var cancellation));

        Assert.Equal(new Application.WholesaleCancellation(Wholesaler, Order, Buyer, PurchaseOrder), cancellation);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tenantId":"x","orderId":"y"}""")]
    [InlineData("""{"tenantId":"0196f1a0-0000-7000-8000-000000000001","orderId":"0196f1a0-0000-7000-8000-000000000002","buyerTenantId":"0196f1a0-0000-7000-8000-000000000003","purchaseOrderId":"0196f1a0-0000-7000-8000-000000000004","currency":"COP","lines":"nope"}""")]
    [InlineData("""{"tenantId":"0196f1a0-0000-7000-8000-000000000001","orderId":"0196f1a0-0000-7000-8000-000000000002","buyerTenantId":"0196f1a0-0000-7000-8000-000000000003","purchaseOrderId":"0196f1a0-0000-7000-8000-000000000004","currency":"","lines":[]}""")]
    public void AMalformedPayloadIsNotReadable(string json) =>
        Assert.False(WholesaleOrderEventPayloads.TryReadApproved(json, out _));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tenantId":1,"orderId":"0196f1a0-0000-7000-8000-000000000002"}""")]
    public void AMalformedCancellationIsNotReadable(string json) =>
        Assert.False(WholesaleOrderEventPayloads.TryReadCancelled(json, out _));
}
