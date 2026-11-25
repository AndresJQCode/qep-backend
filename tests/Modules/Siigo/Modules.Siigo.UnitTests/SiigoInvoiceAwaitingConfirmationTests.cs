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

    // Lo que escribe la versión anterior durante el despliegue, después del relleno de la migración: el motivo, sin unanswered_at.
    private static SiigoInvoice LeftByThePreviousVersion(string errorCode, DateTimeOffset at)
    {
        var invoice = NewPending();
        invoice.BeginSend(at);
        invoice.MarkTransient(errorCode, at);
        return invoice;
    }

    [Theory]
    [InlineData("siigo.invoice.timeout")]
    [InlineData("siigo.invoice.network")]
    [InlineData("siigo.invoice.rate_limited")]
    [InlineData("siigo.invoice.siigo_service")]
    public void ARowThePreviousVersionLeftWithoutTheAnchorStaysToConfirm(string errorCode)
    {
        var invoice = LeftByThePreviousVersion(errorCode, Now);

        Assert.True(invoice.IsAwaitingConfirmation(Now.AddMinutes(1)));
        Assert.Equal(Now.AddMinutes(10), invoice.MissingAvailableAt(Now.AddMinutes(9)));
        var tooEarly = Assert.Throws<SiigoResolveTooEarlyException>(() => invoice.ResolveMissing(Now.AddMinutes(9)));
        Assert.Equal(Now.AddMinutes(10), tooEarly.AvailableAt);

        invoice.BeginSend(Now.AddMinutes(2));
        var rejected = invoice.RecordRejection(
            "siigo.invoice.rejected", "invalid_reference", null, serviceOutage: false, Now.AddMinutes(2));

        Assert.False(rejected);
        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
        Assert.Equal(Now, invoice.UnansweredAt);
        Assert.Equal(Now, invoice.LastUnansweredAt);
        Assert.Equal(Now.AddMinutes(10), invoice.MissingAvailableAt(Now.AddMinutes(3)));
        invoice.ResolveMissing(Now.AddMinutes(10));
        Assert.Equal(SiigoInvoiceStatus.Failed, invoice.Status);
    }

    [Fact]
    public void RecoveringARowThePreviousVersionLeftSaysItBecameToConfirm()
    {
        var invoice = LeftByThePreviousVersion("siigo.invoice.timeout", Now);

        Assert.True(invoice.RecoverInterruptedSend(Now.AddMinutes(1)));

        Assert.Equal(Now, invoice.UnansweredAt);
        Assert.Equal("siigo.invoice.timeout", invoice.ErrorCode);
        Assert.False(invoice.RecoverInterruptedSend(Now.AddMinutes(2)));
    }

    // D-2: este motivo también lo escriben los envíos que nunca salieron.
    [Fact]
    public void AnUnavailableRowWithoutTheAnchorIsNotToConfirm()
    {
        var invoice = LeftByThePreviousVersion("siigo.invoice.unavailable", Now);

        Assert.False(invoice.IsAwaitingConfirmation(Now.AddMinutes(1)));
        Assert.Null(invoice.MissingAvailableAt(Now.AddMinutes(1)));
        Assert.False(invoice.RecoverInterruptedSend(Now.AddMinutes(1)));

        invoice.BeginSend(Now.AddMinutes(2));

        Assert.True(invoice.RecordRejection(
            "siigo.invoice.rejected", "invalid_reference", null, serviceOutage: false, Now.AddMinutes(2)));
        Assert.Equal(SiigoInvoiceStatus.Rejected, invoice.Status);
    }

    [Fact]
    public void ARowBeingSentDoesNotExpire()
    {
        var invoice = NewPending();
        var sentAt = Now + SiigoInvoice.StaleAfter - TimeSpan.FromMinutes(1);
        invoice.BeginSend(sentAt);

        Assert.False(invoice.ExpireIfStale(sentAt.AddMinutes(2)));

        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
        Assert.Equal(sentAt, invoice.SendingSince);
        Assert.Throws<SiigoDomainException>(() => invoice.ResolveMissing(sentAt.AddMinutes(2)));
    }

    [Fact]
    public void ExpiringADeadSendKeepsItAsTheAnchor()
    {
        var invoice = NewPending();
        var sentAt = Now + SiigoInvoice.StaleAfter - TimeSpan.FromMinutes(1);
        invoice.BeginSend(sentAt);
        var expiredAt = sentAt + SiigoInvoice.SendLease + TimeSpan.FromSeconds(1);

        Assert.True(invoice.ExpireIfStale(expiredAt));

        Assert.Equal(SiigoInvoiceStatus.Unknown, invoice.Status);
        Assert.Null(invoice.SendingSince);
        Assert.Equal(sentAt, invoice.UnansweredAt);
        Assert.Equal(sentAt, invoice.LastUnansweredAt);
        var tooEarly = Assert.Throws<SiigoResolveTooEarlyException>(() => invoice.ResolveMissing(expiredAt));
        Assert.Equal(sentAt.AddMinutes(10), tooEarly.AvailableAt);
        invoice.ResolveMissing(sentAt.AddMinutes(10));
        Assert.Equal(SiigoInvoiceStatus.Failed, invoice.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnknownWithARecentUnansweredSendAlsoWaitsAndExistsDoesNot(bool leftByThePreviousVersion)
    {
        var sentAt = Now + SiigoInvoice.StaleAfter - TimeSpan.FromMinutes(1);
        var invoice = leftByThePreviousVersion ? LeftByThePreviousVersion("siigo.invoice.timeout", sentAt) : Unanswered(sentAt);
        var expiredAt = sentAt.AddMinutes(2);
        Assert.True(invoice.ExpireIfStale(expiredAt));

        Assert.Equal(sentAt.AddMinutes(10), invoice.MissingAvailableAt(expiredAt));
        var tooEarly = Assert.Throws<SiigoResolveTooEarlyException>(() => invoice.ResolveMissing(expiredAt));
        Assert.Equal(sentAt.AddMinutes(10), tooEarly.AvailableAt);

        invoice.ResolveExists("FV-1-9", expiredAt);

        Assert.Equal(SiigoInvoiceStatus.Created, invoice.Status);
    }

    [Fact]
    public void ARetainedRejectionKeepsTheExpectedTotalUntilTheNextOutcome()
    {
        var invoice = Unanswered(Now);
        invoice.BeginSend(Now.AddMinutes(1));

        invoice.RecordRejection(
            "siigo.invoice.totals_mismatch", "invalid_total_payments: 288490.5", 288490.5m, serviceOutage: false, Now.AddMinutes(1));

        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
        Assert.Equal(288490.5m, invoice.ExpectedPaymentsTotal);

        invoice.BeginSend(Now.AddMinutes(2));
        invoice.MarkUnanswered("siigo.invoice.timeout", null, Now.AddMinutes(2));

        Assert.Null(invoice.ExpectedPaymentsTotal);
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
