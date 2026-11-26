using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;
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

    // Dos réplicas resuelven a la vez los dos últimos pedidos y ninguna cierra la tanda: al leerla está terminada.
    [Fact]
    public void ABatchLeftOpenWithEveryItemSettledReadsAsFinishedAtTheLastSettlement()
    {
        var batch = Create(Guid.NewGuid(), Guid.NewGuid());
        batch.Items[0].MarkInvoiced("FV-1-1", Now.AddMinutes(2));
        Assert.Null(batch.ReadFinishedAt());

        batch.Items[1].MarkFailed("siigo.invoice.rejected", Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(2), batch.ReadFinishedAt());
        Assert.Null(batch.FinishedAt);
        var summary = SiigoInvoiceBatchSummaryDto.From(batch, new Dictionary<Guid, SiigoInvoice>(), Now.AddMinutes(3));
        Assert.Equal(("Finished", (DateTimeOffset?)Now.AddMinutes(2)), (summary.Status, summary.FinishedAt));
        var detail = SiigoInvoiceBatchDto.From(
            batch, new Dictionary<Guid, SiigoInvoice>(), new Dictionary<Guid, SiigoOrderLabel>(), [], Now.AddMinutes(3));
        Assert.Equal(("Finished", (DateTimeOffset?)Now.AddMinutes(2)), (detail.Status, detail.FinishedAt));
    }

    [Fact]
    public void AClosedBatchKeepsItsStoredFinishTime()
    {
        var batch = Create(Guid.NewGuid());
        batch.Items[0].MarkInvoiced("FV-1-1", Now);
        batch.CloseIfFinished(Now.AddMinutes(1));

        Assert.Equal(Now.AddMinutes(1), batch.ReadFinishedAt());
    }

    [Fact]
    public void AToConfirmItemIsSettledAndClosesTheBatch()
    {
        var batch = Create(Guid.NewGuid());
        var invoiceId = Guid.NewGuid();

        batch.Items[0].MarkToConfirm(invoiceId, "siigo.invoice.timeout", Now);

        var item = batch.Items[0];
        Assert.Equal(SiigoInvoiceBatchItemStatus.ToConfirm, item.Status);
        Assert.Equal(invoiceId, item.InvoiceId);
        Assert.Equal("siigo.invoice.timeout", item.ErrorCode);
        Assert.True(item.IsSettled);
        Assert.True(batch.CloseIfFinished(Now));

        item.MarkFailed("siigo.invoice.rejected", Now);

        Assert.Equal(SiigoInvoiceBatchItemStatus.ToConfirm, item.Status);
    }

    [Fact]
    public void TheItemKeepsItsInvoiceAndTheOrderMarkWarning()
    {
        var items = Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).Items;
        var invoiced = Guid.NewGuid();
        var failed = Guid.NewGuid();

        items[0].MarkInvoiced("FV-1-1", Now, invoiced, true);
        items[1].MarkFailed("siigo.invoice.rejected", Now, failed);
        items[2].MarkInvoiced("FV-1-2", Now);

        Assert.Equal(invoiced, items[0].InvoiceId);
        Assert.True(items[0].OrderMarkFailed);
        Assert.Equal(failed, items[1].InvoiceId);
        Assert.False(items[1].OrderMarkFailed);
        Assert.Null(items[2].InvoiceId);
        Assert.False(items[2].OrderMarkFailed);
    }

    [Fact]
    public void PaymentsTravelOnlyWithASingleOrder()
    {
        const string payments = """[{"kind":"Order"}]""";

        var batch = SiigoInvoiceBatch.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Now, payments);

        Assert.Equal(payments, batch.Items[0].PaymentsJson);
        Assert.Null(Create(Guid.NewGuid()).Items[0].PaymentsJson);
        Assert.Throws<ArgumentException>(() =>
            SiigoInvoiceBatch.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()], Now, payments));
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
