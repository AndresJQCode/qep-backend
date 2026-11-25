using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

public sealed class SiigoInvoiceBatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private static SiigoInvoiceBatch Create(params Guid[] orderIds) =>
        SiigoInvoiceBatch.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), orderIds, Now);

    [Fact]
    public void ABatchStartsWithOnePendingItemPerOrderInTheGivenOrder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var batch = Create(first, second);

        Assert.Equal([first, second], batch.Items.Select(item => item.OrderId));
        Assert.Equal([0, 1], batch.Items.Select(item => item.Position));
        Assert.All(batch.Items, item => Assert.Equal(SiigoInvoiceBatchItemStatus.Pending, item.Status));
        Assert.False(batch.IsFinished);
        Assert.Null(batch.FinishedAt);
    }

    [Fact]
    public void AnEmptyBatchIsRejected() =>
        Assert.Throws<SiigoDomainException>(() => Create());

    [Fact]
    public void MoreThanFiftyOrdersAreRejected() =>
        Assert.Throws<SiigoDomainException>(() => Create([.. Enumerable.Range(0, SiigoInvoiceBatch.MaxItems + 1).Select(_ => Guid.NewGuid())]));

    [Fact]
    public void FiftyOrdersAreAccepted() =>
        Assert.Equal(SiigoInvoiceBatch.MaxItems, Create([.. Enumerable.Range(0, SiigoInvoiceBatch.MaxItems).Select(_ => Guid.NewGuid())]).Items.Count);

    [Fact]
    public void ARepeatedOrderIsRejected()
    {
        var id = Guid.NewGuid();
        Assert.Throws<SiigoDomainException>(() => Create(id, id));
    }

    [Fact]
    public void AnEmptyOrderIdIsRejected() =>
        Assert.Throws<SiigoDomainException>(() => Create(Guid.Empty));

    [Fact]
    public void TheBatchFinishesWhenEveryItemIsSettledAndOnlyThen()
    {
        var batch = Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        batch.Items[0].MarkInvoiced("FV-1-1", Now);
        batch.Items[1].MarkSkipped("siigo.invoice.not_ready", ["customer_document"], Now);
        Assert.False(batch.CloseIfFinished(Now));
        batch.Items[2].MarkFailed("siigo.invoice.rejected", Now);

        Assert.True(batch.CloseIfFinished(Now.AddMinutes(1)));
        Assert.Equal(Now.AddMinutes(1), batch.FinishedAt);
        Assert.False(batch.CloseIfFinished(Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(1), batch.FinishedAt);
    }

    [Fact]
    public void ASkippedItemKeepsItsCodeAndMissingList()
    {
        var item = Create(Guid.NewGuid()).Items[0];

        item.MarkSkipped("siigo.invoice.not_ready", ["customer_document", "product_unlinked"], Now);

        Assert.Equal(SiigoInvoiceBatchItemStatus.Skipped, item.Status);
        Assert.Equal("siigo.invoice.not_ready", item.ErrorCode);
        Assert.Equal(["customer_document", "product_unlinked"], item.MissingCodes);
    }

    [Fact]
    public void ASettledItemDoesNotChangeAgain()
    {
        var item = Create(Guid.NewGuid()).Items[0];
        item.MarkInvoiced("FV-1-1", Now);

        item.MarkFailed("siigo.invoice.rejected", Now);

        Assert.Equal(SiigoInvoiceBatchItemStatus.Invoiced, item.Status);
        Assert.Equal("FV-1-1", item.InvoiceNumber);
        Assert.Null(item.ErrorCode);
    }
}
