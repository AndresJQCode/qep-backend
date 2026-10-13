using Modules.Pipeline.Domain;

namespace Modules.Pipeline.UnitTests;

/// <summary>Spec 2026-10-13 §4.4: la foto que Pipeline guarda de cada cotización que le llegó por evento.</summary>
public sealed class DealQuotationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 15, 0, 0, TimeSpan.Zero);

    private static DealQuotation Sent() =>
        DealQuotation.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "COT-0001", Guid.CreateVersion7(), 1_000m, "COP", Now);

    [Fact]
    public void ANewSnapshotIsSentAndUnlinked()
    {
        var quotation = Sent();

        Assert.Equal((DealQuotationStatus.Sent, (Guid?)null, "COT-0001", 1_000m), (quotation.Status, quotation.DealId, quotation.QuotationNumber, quotation.Total));
    }

    // Un sent que llega después del converted (reintento, D25) no la devuelve a Sent.
    [Fact]
    public void ConvertedIsStickyAndARefreshUpdatesTheFigures()
    {
        var quotation = Sent();
        var orderId = Guid.CreateVersion7();
        var customerId = Guid.CreateVersion7();

        quotation.Refresh("COT-0001", customerId, 1_200m, "USD", converted: true, orderId, Now.AddHours(1));
        quotation.Refresh("COT-0001", customerId, 1_300m, "USD", converted: false, null, Now.AddHours(2));

        Assert.Equal((DealQuotationStatus.Converted, (Guid?)orderId, customerId), (quotation.Status, quotation.OrderId, quotation.CustomerId));
        Assert.Equal((1_300m, "USD", Now.AddHours(2)), (quotation.Total, quotation.Currency, quotation.UpdatedAt));
    }

    // Entrega desordenada o reintento: el converted del MISMO pedido que ya se anuló no borra la anulación ni el pedido.
    [Fact]
    public void ARepeatedConvertedOfACancelledOrderKeepsTheCancellation()
    {
        var quotation = Sent();
        var orderId = Guid.CreateVersion7();
        var customerId = quotation.CustomerId;
        quotation.Refresh("COT-0001", customerId, 1_000m, "COP", converted: true, orderId, Now);
        quotation.MarkOrderCancelled(Now.AddDays(1));

        quotation.Refresh("COT-0001", customerId, 1_000m, "COP", converted: true, orderId, Now.AddDays(2));
        quotation.Refresh("COT-0001", customerId, 1_000m, "COP", converted: true, null, Now.AddDays(3));

        Assert.Equal(((Guid?)orderId, (DateTimeOffset?)Now.AddDays(1)), (quotation.OrderId, quotation.OrderCancelledAt));
    }

    // Un pedido distinto sí es una conversión nueva: la anulación anterior ya no aplica.
    [Fact]
    public void AConvertedWithANewOrderClearsTheOldCancellation()
    {
        var quotation = Sent();
        var customerId = quotation.CustomerId;
        quotation.Refresh("COT-0001", customerId, 1_000m, "COP", converted: true, Guid.CreateVersion7(), Now);
        quotation.MarkOrderCancelled(Now.AddDays(1));
        var newOrder = Guid.CreateVersion7();

        quotation.Refresh("COT-0001", customerId, 1_000m, "COP", converted: true, newOrder, Now.AddDays(2));

        Assert.Equal(((Guid?)newOrder, (DateTimeOffset?)null), (quotation.OrderId, quotation.OrderCancelledAt));
    }

    [Fact]
    public void LinkUnlinkAndCancelTheOrder()
    {
        var quotation = Sent();
        var dealId = Guid.CreateVersion7();

        quotation.LinkTo(dealId, Now);
        Assert.Equal((Guid?)dealId, quotation.DealId);

        quotation.Unlink(Now);
        quotation.MarkOrderCancelled(Now.AddDays(1));

        Assert.Null(quotation.DealId);
        Assert.Equal((DateTimeOffset?)Now.AddDays(1), quotation.OrderCancelledAt);
    }
}
