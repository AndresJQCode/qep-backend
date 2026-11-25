using Modules.Siigo.Application.Invoicing;
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

    // El ítem se cerró antes de lo último que le pasó a la fila (otro reenvío, el vencimiento, una resolución): manda la fila.
    private static SiigoInvoiceBatchItem ToConfirm(SiigoInvoice invoice) => ToConfirm(invoice, "siigo.invoice.network", Now.AddMinutes(-1));

    private static SiigoInvoiceBatchItem ToConfirm(SiigoInvoice invoice, string? errorCode, DateTimeOffset settledAt)
    {
        var item = NewItem();
        item.MarkToConfirm(invoice.Id, errorCode, settledAt);
        return item;
    }

    // Un reenvío que no se pudo hacer (conexión, permiso) no toca la fila: su código es el de un envío anterior y no dice por qué no salió
    // éste. Con la misma hora también manda el ítem: la recuperación y el envío normal copian el de la fila.
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void AnItemSettledAfterTheLastChangeOfItsRowShowsItsOwnCode(int minutesAfter)
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice, "siigo.connection_unavailable", Now.AddMinutes(minutesAfter));

        var state = item.Effective(invoice, Now.AddMinutes(minutesAfter));

        Assert.Equal(
            (SiigoInvoiceBatchItemStatus.ToConfirm, "siigo.connection_unavailable"), (state.Status, state.ErrorCode));
    }

    // Los códigos de Siigo de la fila son del envío que dejó su código: junto al motivo de un reenvío que no salió mezclarían dos
    // intentos.
    [Theory]
    [InlineData("siigo.connection_unavailable")]
    [InlineData("siigo.invoice_batch.forbidden")]
    public void ACodeOfTheItemThatIsNotTheRowOneTravelsWithoutTheSiigoErrorsOfTheRow(string itemCode)
    {
        var invoice = UnansweredByADeadService();
        var item = ToConfirm(invoice, itemCode, Now.AddMinutes(5));

        var dto = DtoOf(item, invoice, Now.AddMinutes(5));

        Assert.Equal(itemCode, dto.ErrorCode);
        Assert.Empty(dto.SiigoErrors);
    }

    // El envío normal copia el código de la fila, y un ítem sin código muestra el de ella: son del mismo intento.
    [Theory]
    [InlineData("siigo.invoice.siigo_service")]
    [InlineData(null)]
    public void TheCodeOfAnUnchangedRowKeepsItsSiigoErrors(string? itemCode)
    {
        var invoice = UnansweredByADeadService();
        var item = ToConfirm(invoice, itemCode, Now.AddMinutes(5));

        var dto = DtoOf(item, invoice, Now.AddMinutes(5));

        Assert.Equal("siigo.invoice.siigo_service", dto.ErrorCode);
        Assert.Equal(["documents_service"], dto.SiigoErrors);
    }

    [Fact]
    public void ARowThatChangedLaterTravelsWithItsSiigoErrors()
    {
        var invoice = UnansweredByADeadService();
        var item = ToConfirm(invoice, "siigo.connection_unavailable", Now.AddMinutes(-1));

        var dto = DtoOf(item, invoice, Now.AddMinutes(1));

        Assert.Equal("siigo.invoice.siigo_service", dto.ErrorCode);
        Assert.Equal(["documents_service"], dto.SiigoErrors);
    }

    // Fuera de por confirmar nada cambia: una que alguien declaró inexistente sigue mostrando lo que contestó Siigo.
    [Fact]
    public void AResolvedMissingItemKeepsTheSiigoErrorsOfItsRow()
    {
        var invoice = UnansweredByADeadService();
        var item = ToConfirm(invoice);
        invoice.ResolveMissing(Now.AddMinutes(10));

        var dto = DtoOf(item, invoice, Now.AddMinutes(10));

        Assert.Equal("siigo.invoice.resolved_missing", dto.ErrorCode);
        Assert.Equal(["documents_service"], dto.SiigoErrors);
    }

    private static SiigoInvoice UnansweredByADeadService()
    {
        var invoice = NewInvoice();
        invoice.BeginSend(Now);
        invoice.MarkUnanswered("siigo.invoice.siigo_service", "documents_service", Now);
        return invoice;
    }

    private static SiigoInvoiceBatchItemDto DtoOf(SiigoInvoiceBatchItem item, SiigoInvoice invoice, DateTimeOffset now) =>
        SiigoInvoiceBatchItemDto.From(item, item.Effective(invoice, now), invoice, null, false);

    [Fact]
    public void ARowThatChangesAfterTheItemSettledDecidesTheCode()
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice, "siigo.connection_unavailable", Now.AddMinutes(5));
        invoice.BeginSend(Now.AddMinutes(10));
        invoice.MarkUnanswered("siigo.invoice.siigo_service", "documents_service", Now.AddMinutes(11));

        var state = item.Effective(invoice, Now.AddMinutes(12));

        Assert.Equal("siigo.invoice.siigo_service", state.ErrorCode);
    }

    [Fact]
    public void ARowThatExpiresAfterTheItemSettledDecidesTheCode()
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice, "siigo.invoice_batch.forbidden", Now.AddMinutes(5));
        invoice.ExpireIfStale(Now.AddHours(25));

        var state = item.Effective(invoice, Now.AddHours(25));

        Assert.Equal("siigo.invoice.timeout", state.ErrorCode);
    }

    [Fact]
    public void AnItemWithoutACodeTakesTheOneOfItsUnchangedRow()
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice, null, Now.AddMinutes(5));

        var state = item.Effective(invoice, Now.AddMinutes(5));

        Assert.Equal("siigo.invoice.timeout", state.ErrorCode);
    }

    [Fact]
    public void ARowThatChangedWithoutACodeKeepsTheItemCode()
    {
        var invoice = NewInvoice();
        var item = ToConfirm(invoice, "siigo.connection_unavailable", Now);
        invoice.BeginSend(Now.AddMinutes(1));

        var state = item.Effective(invoice, Now.AddMinutes(1) + SiigoInvoice.SendLease);

        Assert.Null(invoice.ErrorCode);
        Assert.Equal("siigo.connection_unavailable", state.ErrorCode);
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
    public void AToConfirmWhoseInvoiceChangedLaterStaysToConfirmWithTheRowCodeAndItsActions()
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

    // Como en la pantalla del pedido: una que nunca salió (falla de configuración) se reintenta, pero no ofrece «Existe / No existe».
    [Fact]
    public void AToConfirmWhoseInvoiceNeverLeftIsNotOfferedForResolution()
    {
        var invoice = NewInvoice();
        invoice.BeginSend(Now);
        invoice.MarkTransient("siigo.invoice.unavailable", Now);
        var item = ToConfirm(invoice);

        var state = item.Effective(invoice, Now.AddMinutes(1));

        Assert.Equal(
            new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.ToConfirm, "siigo.invoice.unavailable", null, true, false, null), state);
    }

    // Pasadas las 24 horas es Unknown aunque nadie la haya leído desde el pedido: se ofrece resolver.
    [Fact]
    public void AToConfirmWhoseStalePendingWasNotExpiredYetCanBeResolved()
    {
        var invoice = NewInvoice();
        invoice.BeginSend(Now);
        invoice.MarkTransient("siigo.invoice.unavailable", Now);
        var item = ToConfirm(invoice);

        var state = item.Effective(invoice, Now.AddHours(25));

        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
        Assert.Equal(
            new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.ToConfirm, "siigo.invoice.unavailable", null, false, true, null), state);
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

    // Spec 2026-11-25 §1: se resolvió como existente (o salió) y después se anuló en Siigo: el pedido ya no está facturado por esa tanda.
    [Fact]
    public void AToConfirmWhoseInvoiceWasAnnulledLooksFailedWithTheRowCode()
    {
        var invoice = Unanswered();
        var item = ToConfirm(invoice);
        invoice.MarkCreated("id-1", "FV-1-9", 100m, null, Now);
        invoice.MarkAnnulled(false, Now.AddMinutes(1));

        var state = item.Effective(invoice, Now.AddMinutes(1));

        Assert.Equal(
            new SiigoBatchItemState(SiigoInvoiceBatchItemStatus.Failed, "siigo.invoice.annulled_in_siigo", null, false, false, null),
            state);
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
