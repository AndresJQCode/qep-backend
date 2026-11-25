using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-24 §3: una factura de la que no se sabe si salió queda «por confirmar» (<c>Pending</c> con
/// <c>UnansweredAt</c>), nunca <c>Rejected</c>, y la resuelve o la reenvía una persona.</summary>
public sealed class SiigoInvoiceAwaitingConfirmationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    private static SiigoInvoice NewPending() =>
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

    // Una fila con un envío que salió y no tuvo respuesta en el momento dado.
    private static SiigoInvoice Unanswered(DateTimeOffset at)
    {
        var invoice = NewPending();
        invoice.BeginSend(at);
        invoice.MarkUnanswered("siigo.invoice.timeout", null, at);
        return invoice;
    }

    [Fact]
    public void AnUnansweredSendKeepsTheFirstMomentAndMovesTheLast()
    {
        var invoice = NewPending();

        invoice.BeginSend(Now);
        Assert.True(invoice.MarkUnanswered("siigo.invoice.timeout", null, Now));
        invoice.BeginSend(Now.AddMinutes(1));
        Assert.False(invoice.MarkUnanswered("siigo.invoice.siigo_service", "documents_service", Now.AddMinutes(1)));

        Assert.Equal(Now, invoice.UnansweredAt);
        Assert.Equal(Now.AddMinutes(1), invoice.LastUnansweredAt);
        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
        Assert.Equal(("siigo.invoice.siigo_service", "documents_service"), (invoice.ErrorCode, invoice.ErrorDetail));
        Assert.False(invoice.IsBeingSent(Now.AddMinutes(1)));
    }

    [Fact]
    public void ARejectionAfterAnUnansweredAttemptStaysPending()
    {
        var invoice = Unanswered(Now);
        invoice.BeginSend(Now.AddMinutes(2));

        var rejected = invoice.RecordRejection(
            "siigo.invoice.rejected", "invalid_reference", null, serviceOutage: false, Now.AddMinutes(2));

        Assert.False(rejected);
        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
        Assert.Equal("siigo.invoice.rejected", invoice.ErrorCode);
        Assert.Equal("invalid_reference", invoice.ErrorDetail);
        Assert.Equal(Now, invoice.LastUnansweredAt);
        Assert.False(invoice.IsBeingSent(Now.AddMinutes(2)));
        Assert.True(invoice.IsLive);
    }

    [Fact]
    public void AServiceOutageOnTheFirstAttemptAwaitsConfirmation()
    {
        var invoice = NewPending();
        invoice.BeginSend(Now);

        var rejected = invoice.RecordRejection("siigo.invoice.siigo_service", "documents_service", null, serviceOutage: true, Now);

        Assert.False(rejected);
        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
        Assert.Equal(Now, invoice.UnansweredAt);
        Assert.True(invoice.IsAwaitingConfirmation(Now));
    }

    [Fact]
    public void ADataRejectionOnTheFirstAttemptIsStillRejected()
    {
        var invoice = NewPending();
        invoice.BeginSend(Now);

        var rejected = invoice.RecordRejection("siigo.invoice.rejected", "invalid_reference", null, serviceOutage: false, Now);

        Assert.True(rejected);
        Assert.Equal(SiigoInvoiceStatus.Rejected, invoice.Status);
        Assert.Null(invoice.UnansweredAt);
        Assert.False(invoice.IsLive);
        Assert.False(invoice.IsAwaitingConfirmation(Now));
    }

    [Fact]
    public void AnExpiredSendWithoutOutcomeIsRecoveredAsUnanswered()
    {
        var dead = NewPending();
        dead.BeginSend(Now);
        var later = Now + SiigoInvoice.SendLease + TimeSpan.FromSeconds(1);

        Assert.True(dead.RecoverInterruptedSend(later));

        Assert.Equal(later, dead.UnansweredAt);
        Assert.Equal(later, dead.LastUnansweredAt);
        Assert.Equal("siigo.invoice.interrupted", dead.ErrorCode);
        Assert.Null(dead.ErrorDetail);
        Assert.Null(dead.SendingSince);

        var inFlight = NewPending();
        inFlight.BeginSend(Now);

        Assert.False(inFlight.RecoverInterruptedSend(Now.AddMinutes(1)));

        Assert.Null(inFlight.UnansweredAt);
        Assert.Null(inFlight.ErrorCode);
        Assert.Equal(Now, inFlight.SendingSince);
    }

    [Fact]
    public void ASecondInterruptedSendKeepsTheFirstMoment()
    {
        var invoice = Unanswered(Now);
        invoice.BeginSend(Now.AddMinutes(1));
        var later = Now.AddMinutes(1) + SiigoInvoice.SendLease;

        Assert.False(invoice.RecoverInterruptedSend(later));

        Assert.Equal(Now, invoice.UnansweredAt);
        Assert.Equal(later, invoice.LastUnansweredAt);
    }

    [Fact]
    public void MissingWaitsTenMinutesAfterTheLastUnansweredAttemptAndExistsDoesNot()
    {
        var missing = Unanswered(Now);

        var tooEarly = Assert.Throws<SiigoResolveTooEarlyException>(() => missing.ResolveMissing(Now.AddMinutes(9)));

        Assert.Equal(Now.AddMinutes(10), tooEarly.AvailableAt);
        Assert.Equal("siigo.invoice.resolve_too_early", tooEarly.Code);
        Assert.True(tooEarly.ProblemExtensions.ContainsKey("details"));
        Assert.Equal(SiigoInvoiceStatus.Pending, missing.Status);
        Assert.Equal(Now.AddMinutes(10), missing.MissingAvailableAt(Now.AddMinutes(9)));
        Assert.Null(missing.MissingAvailableAt(Now.AddMinutes(10)));

        missing.ResolveMissing(Now.AddMinutes(10));

        Assert.Equal(SiigoInvoiceStatus.Failed, missing.Status);

        var exists = Unanswered(Now);

        exists.ResolveExists("FV-1-9", Now.AddMinutes(1));

        Assert.Equal(SiigoInvoiceStatus.Created, exists.Status);
        Assert.Equal("FV-1-9", exists.Number);
        Assert.False(exists.IsAwaitingConfirmation(Now.AddMinutes(1)));
    }

    [Fact]
    public void AnUnknownResolvesMissingAtOnce()
    {
        var invoice = Unanswered(Now);
        invoice.ExpireIfStale(Now.AddHours(25));

        Assert.Null(invoice.MissingAvailableAt(Now.AddHours(25)));
        invoice.ResolveMissing(Now.AddHours(25));

        Assert.Equal(SiigoInvoiceStatus.Failed, invoice.Status);
    }

    // D-1 (owner, 2026-11-24): un envío que murió y nadie reenvió tiene el reclamo vencido y todavía no tiene UnansweredAt.
    [Fact]
    public void ADeadSendThatNobodyResentAwaitsConfirmationAndWaitsTenMinutes()
    {
        var invoice = NewPending();
        invoice.BeginSend(Now);
        var later = Now + SiigoInvoice.SendLease + TimeSpan.FromSeconds(1);

        Assert.True(invoice.IsAwaitingConfirmation(later));
        Assert.Equal(Now.AddMinutes(10), invoice.MissingAvailableAt(later));
        var tooEarly = Assert.Throws<SiigoResolveTooEarlyException>(() => invoice.ResolveMissing(later));
        Assert.Equal(Now.AddMinutes(10), tooEarly.AvailableAt);
    }

    [Fact]
    public void ASendInFlightIsNeitherToConfirmNorResolvable()
    {
        var invoice = Unanswered(Now);
        invoice.BeginSend(Now.AddMinutes(1));

        Assert.False(invoice.IsAwaitingConfirmation(Now.AddMinutes(2)));
        Assert.False(invoice.IsResolvable(Now.AddMinutes(2)));
        Assert.Null(invoice.MissingAvailableAt(Now.AddMinutes(2)));
    }

    // La de flete que espera a la del pedido nunca salió: no está por confirmar y «No existe» no tiene que esperar.
    [Fact]
    public void APendingThatNeverLeftIsResolvableAtOnceAndNotToConfirm()
    {
        var invoice = NewPending();
        invoice.BeginSend(Now);
        invoice.MarkTransient(null, Now);

        Assert.False(invoice.IsAwaitingConfirmation(Now));
        Assert.True(invoice.IsResolvable(Now));
        Assert.Null(invoice.MissingAvailableAt(Now));
    }

    [Fact]
    public void OnlyAPendingNotBeingSentAndNotStaleCanBeResent()
    {
        var inFlight = NewPending();
        inFlight.BeginSend(Now);
        var created = NewPending();
        created.MarkCreated("id-1", "FV-1-123", 100m, null, Now);
        var unanswered = Unanswered(Now);

        Assert.False(inFlight.CanResend(Now.AddMinutes(1)));
        Assert.False(created.CanResend(Now));
        Assert.True(unanswered.CanResend(Now.AddMinutes(1)));
        Assert.True(unanswered.CanResend(Now + SiigoInvoice.StaleAfter));
        Assert.False(unanswered.CanResend(Now + SiigoInvoice.StaleAfter + TimeSpan.FromSeconds(1)));
    }
}
