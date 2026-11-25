using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-24 §2: el estado que se devuelve de un ítem por confirmar se calcula al leer, con la fila de la factura
/// enlazada.</summary>
public sealed class SiigoBatchItemEffectiveStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static SiigoInvoice NewInvoice() =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            SiigoInvoiceKind.Order,
            """{"a":1}""",
            7,
            Guid.CreateVersion7(),
            Now);

    private static SiigoInvoice Unanswered()
    {
        var invoice = NewInvoice();
        invoice.BeginSend(Now);
        invoice.MarkUnanswered("siigo.invoice.timeout", null, Now);
        return invoice;
    }

    private static SiigoInvoiceBatchItem NewItem() =>
        SiigoInvoiceBatch.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Now).Items[0];

    private static SiigoInvoiceBatchItem ToConfirm(SiigoInvoice invoice)
    {
        var item = NewItem();
        item.MarkToConfirm(invoice.Id, "siigo.invoice.network", Now);
        return item;
    }

    [Fact]
    public void AToConfirmWhoseInvoiceIsBeingSentLooksSending()
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice);
        invoice.BeginSend(Now.AddMinutes(1));

        var state = item.Effective(invoice, Now.AddMinutes(2));

        Assert.Equal(new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Sending, null, null, false, false, null), state);
    }

    [Fact]
    public void AToConfirmWhoseInvoiceIsPendingStaysToConfirmWithTheRowCodeAndItsActions()
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice);

        var state = item.Effective(invoice, Now.AddMinutes(1));

        Assert.Equal(
            new SiigoBatchItemState(
                SiigoInvoiceBatchItemStatus.ToConfirm, "siigo.invoice.timeout", null, true, true, Now + SiigoInvoice.MissingResolutionDelay),
            state);
    }

    // Un primer envío cortado o muerto: la fila no tiene código, y el del ítem es lo único que dice qué pasó.
    [Fact]
    public void AToConfirmWhoseRowHasNoCodeKeepsTheItemCode()
    {
        var invoice = NewInvoice();
        invoice.BeginSend(Now);
        var item = NewItem();
        item.MarkToConfirm(invoice.Id, "siigo.invoice.interrupted", Now);

        var state = item.Effective(invoice, Now + SiigoInvoice.SendLease);

        Assert.Null(invoice.ErrorCode);
        Assert.Equal((SiigoInvoiceBatchItemStatus.ToConfirm, "siigo.invoice.interrupted"), (state.Status, state.ErrorCode));
    }

    [Fact]
    public void AToConfirmWhoseInvoiceIsUnknownCanOnlyBeResolved()
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice);
        invoice.ExpireIfStale(Now.AddHours(25));

        var state = item.Effective(invoice, Now.AddHours(25));

        Assert.Equal(
            new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.ToConfirm, "siigo.invoice.timeout", null, false, true, null), state);
    }

    [Fact]
    public void AToConfirmWhoseInvoiceBecameUnknownRightAfterASendStillSaysSinceWhenMissingCanBeDeclared()
    {
        var invoice = NewInvoice();
        var sentAt = Now + SiigoInvoice.StaleAfter - TimeSpan.FromMinutes(1);
        invoice.BeginSend(sentAt);
        invoice.MarkUnanswered("siigo.invoice.timeout", null, sentAt);
        var item = ToConfirm(invoice);
        invoice.ExpireIfStale(sentAt.AddMinutes(2));

        var state = item.Effective(invoice, sentAt.AddMinutes(2));

        Assert.Equal(
            new SiigoBatchItemState(
                SiigoInvoiceBatchItemStatus.ToConfirm, "siigo.invoice.timeout", null, false, true, sentAt.AddMinutes(10)),
            state);
    }

    [Theory]
    [InlineData(SiigoInvoiceStatus.Created)]
    [InlineData(SiigoInvoiceStatus.Stamped)]
    [InlineData(SiigoInvoiceStatus.StampRejected)]
    public void AToConfirmWhoseInvoiceIsCreatedLooksInvoicedWithTheRowNumber(SiigoInvoiceStatus status)
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice);
        invoice.MarkCreated("id-1", "FV-1-9", 100m, status == SiigoInvoiceStatus.Stamped ? "cufe" : null, Now);
        if (status == SiigoInvoiceStatus.StampRejected)
        {
            invoice.MarkStampRejected("dian.rejected", "Regla 90", Now);
        }

        var state = item.Effective(invoice, Now);

        Assert.Equal(status, invoice.Status);
        Assert.Equal(new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Invoiced, null, "FV-1-9", false, false, null), state);
    }

    [Fact]
    public void AToConfirmWhoseInvoiceWasResolvedMissingLooksFailedWithResolvedMissing()
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice);
        invoice.ResolveMissing(Now.AddMinutes(10));

        var state = item.Effective(invoice, Now.AddMinutes(10));

        Assert.Equal(
            new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Failed, "siigo.invoice.resolved_missing", null, false, false, null), state);
    }

    [Fact]
    public void AToConfirmWhoseInvoiceIsRejectedLooksFailedWithTheRowCode()
    {
        var invoice = NewInvoice();
        var item = ToConfirm(invoice);
        invoice.MarkRejected("siigo.invoice.rejected", "invalid_reference", null, Now);

        var state = item.Effective(invoice, Now);

        Assert.Equal(new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Failed, "siigo.invoice.rejected", null, false, false, null), state);
    }

    [Fact]
    public void AnyOtherStoredStatusIsShownAsStored()
    {
        var invoice = Unanswered();
        var pending = NewItem();
        var failed = NewItem();
        failed.MarkFailed("siigo.invoice.rejected", Now, invoice.Id);
        var invoiced = NewItem();
        invoiced.MarkInvoiced("FV-1-1", Now, invoice.Id);

        Assert.Equal(
            new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Pending, null, null, false, false, null), pending.Effective(invoice, Now));
        Assert.Equal(
            new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Failed, "siigo.invoice.rejected", null, false, false, null),
            failed.Effective(invoice, Now));
        Assert.Equal(
            new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Invoiced, null, "FV-1-1", false, false, null),
            invoiced.Effective(invoice, Now));
    }

    [Fact]
    public void AnInvoiceOfAnotherIdIsIgnored()
    {
        var item = ToConfirm(Unanswered());
        var other = Unanswered();
        other.MarkCreated("id-9", "FV-1-99", 100m, null, Now);
        var stored = new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.ToConfirm, "siigo.invoice.network", null, false, false, null);

        Assert.Equal(stored, item.Effective(other, Now));
        Assert.Equal(stored, item.Effective(null, Now));
    }

    [Fact]
    public void TheCountOrderIsFixed() =>
        Assert.Equal(
            [
                SiigoInvoiceBatchItemStatus.Pending,
                SiigoInvoiceBatchItemStatus.Sending,
                SiigoInvoiceBatchItemStatus.Invoiced,
                SiigoInvoiceBatchItemStatus.ToConfirm,
                SiigoInvoiceBatchItemStatus.Failed,
                SiigoInvoiceBatchItemStatus.Skipped,
            ],
            SiigoBatchItemState.CountOrder);
}
