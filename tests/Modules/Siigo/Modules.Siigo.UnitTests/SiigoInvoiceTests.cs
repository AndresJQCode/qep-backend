using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §5.4 y §5.6: los estados de una factura de Siigo y lo que cada transición deja.</summary>
public sealed class SiigoInvoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static SiigoInvoice NewPending(SiigoInvoiceKind kind = SiigoInvoiceKind.Order) =>
        SiigoInvoice.PendingFor(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), kind, """{"a":1}""", 7, Guid.CreateVersion7(), Now);

    private static string CodeOf(Action action) => Assert.Throws<SiigoDomainException>(action).Code;

    [Fact]
    public void ANewInvoiceIsPendingWithItsKeyAndItsBytesAndIsBeingSent()
    {
        var invoice = NewPending();

        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
        Assert.Equal(SiigoIdempotencyKey.For(invoice.Id), invoice.IdempotencyKey);
        Assert.Equal("""{"a":1}""", invoice.RequestJson);
        Assert.Equal(7, invoice.OrderVersion);
        Assert.Equal(0, invoice.Attempts);
        Assert.True(invoice.IsLive);
        Assert.True(invoice.IsOpen);
        Assert.False(invoice.OrderChanged);
    }

    [Fact]
    public void BeginningToSendCountsTheAttemptAndClaimsTheRowUntilTheLeaseEnds()
    {
        var invoice = NewPending();

        invoice.BeginSend(Now);

        Assert.Equal(1, invoice.Attempts);
        Assert.True(invoice.IsBeingSent(Now.AddSeconds(30)));
        Assert.False(invoice.IsBeingSent(Now + SiigoInvoice.SendLease + TimeSpan.FromSeconds(1)));

        invoice.MarkTransient(null, Now.AddSeconds(31));

        Assert.False(invoice.IsBeingSent(Now.AddSeconds(32)));
        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);
    }

    [Fact]
    public void ACreatedInvoiceKeepsItsNumberIdAndTotal()
    {
        var invoice = NewPending();

        invoice.MarkCreated("id-1", "FV-1-123", 288490.5m, null, Now);

        Assert.Equal(SiigoInvoiceStatus.Created, invoice.Status);
        Assert.Equal(("id-1", "FV-1-123", 288490.5m), (invoice.SiigoInvoiceId, invoice.Number, invoice.SiigoTotal));
        Assert.Null(invoice.Cufe);
        Assert.False(invoice.IsBeingSent(Now));
    }

    [Fact]
    public void ACreatedInvoiceWithACufeIsStamped()
    {
        var invoice = NewPending();

        invoice.MarkCreated("id-1", "FV-1-123", 100m, "cufe-abc", Now);

        Assert.Equal(SiigoInvoiceStatus.Stamped, invoice.Status);
        Assert.Equal("cufe-abc", invoice.Cufe);
    }

    [Fact]
    public void ARejectionKeepsTheCodeTheDetailAndTheExpectedTotalAndFreesTheOrder()
    {
        var invoice = NewPending();

        invoice.MarkRejected("siigo.invoice.totals_mismatch", "invalid_total_payments: 288490.5", 288490.5m, Now);

        Assert.Equal(SiigoInvoiceStatus.Rejected, invoice.Status);
        Assert.Equal("siigo.invoice.totals_mismatch", invoice.ErrorCode);
        Assert.Equal(288490.5m, invoice.ExpectedPaymentsTotal);
        Assert.False(invoice.IsLive);
        Assert.False(invoice.IsOpen);
    }

    [Fact]
    public void TheErrorDetailIsCut()
    {
        var invoice = NewPending();

        invoice.MarkRejected("x", new string('e', 5000), null, Now);

        Assert.Equal(SiigoInvoice.ErrorDetailMaxLength, invoice.ErrorDetail!.Length);
    }

    [Fact]
    public void APendingOlderThanADayBecomesUnknown()
    {
        var invoice = NewPending();

        Assert.False(invoice.ExpireIfStale(Now.AddHours(23).AddMinutes(59)));
        Assert.Equal(SiigoInvoiceStatus.Pending, invoice.Status);

        Assert.True(invoice.ExpireIfStale(Now.AddHours(24).AddSeconds(1)));
        Assert.Equal(SiigoInvoiceStatus.Unknown, invoice.Status);
        Assert.True(invoice.IsOpen);
        Assert.True(invoice.IsLive);
    }

    [Fact]
    public void OnlyAPendingExpires()
    {
        var invoice = NewPending();
        invoice.MarkCreated("id-1", "FV-1-123", 100m, null, Now);

        Assert.False(invoice.ExpireIfStale(Now.AddDays(5)));
        Assert.Equal(SiigoInvoiceStatus.Created, invoice.Status);
    }

    [Fact]
    public void OnlyAnUnknownOrAPendingNotBeingSentCanBeResolved()
    {
        var sending = NewPending();
        sending.BeginSend(Now);
        var created = NewPending();
        created.MarkCreated("id-1", "FV-1-123", 100m, null, Now);
        var rejected = NewPending();
        rejected.MarkRejected("x", "y", null, Now);

        foreach (var invoice in new[] { sending, created, rejected })
        {
            Assert.Equal("siigo.invoice.state_invalid", CodeOf(() => invoice.ResolveExists("FV-1-999", Now)));
            Assert.Equal("siigo.invoice.state_invalid", CodeOf(() => invoice.ResolveMissing(Now)));
        }

        var idle = NewPending();

        idle.ResolveMissing(Now);

        Assert.Equal(SiigoInvoiceStatus.Failed, idle.Status);
    }

    [Fact]
    public void TheSendLeaseIsEightMinutes() => Assert.Equal(TimeSpan.FromMinutes(8), SiigoInvoice.SendLease);

    [Fact]
    public void AnUnknownResolvedAsExistsBecomesCreatedWithThatNumber()
    {
        var invoice = NewPending();
        invoice.ExpireIfStale(Now.AddDays(2));

        invoice.ResolveExists(" FV-1-999 ", Now.AddDays(2));

        Assert.Equal(SiigoInvoiceStatus.Created, invoice.Status);
        Assert.Equal("FV-1-999", invoice.Number);
        Assert.Null(invoice.SiigoInvoiceId);
    }

    [Fact]
    public void AnUnknownResolvedAsExistsNeedsANumber()
    {
        var invoice = NewPending();
        invoice.ExpireIfStale(Now.AddDays(2));

        Assert.Equal("siigo.invoice.state_invalid", CodeOf(() => invoice.ResolveExists(" ", Now)));
        Assert.Equal(SiigoInvoiceStatus.Unknown, invoice.Status);
    }

    [Fact]
    public void AnUnknownResolvedAsMissingBecomesFailedAndFreesTheOrder()
    {
        var invoice = NewPending();
        invoice.ExpireIfStale(Now.AddDays(2));

        invoice.ResolveMissing(Now.AddDays(2));

        Assert.Equal(SiigoInvoiceStatus.Failed, invoice.Status);
        Assert.False(invoice.IsLive);
    }

    [Fact]
    public void AStampRetryMovesCreatedToStampedOrStampRejected()
    {
        var stamped = NewPending();
        stamped.MarkCreated("id-1", "FV-1-123", 100m, null, Now);
        var rejected = NewPending();
        rejected.MarkCreated("id-2", "FV-1-124", 100m, null, Now);

        stamped.MarkStamped("cufe-1", Now);
        rejected.MarkStampRejected("dian.rejected", "Regla 90", Now);

        Assert.Equal((SiigoInvoiceStatus.Stamped, "cufe-1"), (stamped.Status, stamped.Cufe));
        Assert.Equal((SiigoInvoiceStatus.StampRejected, "dian.rejected"), (rejected.Status, rejected.ErrorCode));
        // Una rechazada por la DIAN se vuelve a estampar desde Siigo.
        rejected.MarkStamped("cufe-2", Now);
        Assert.Equal(SiigoInvoiceStatus.Stamped, rejected.Status);
        Assert.Null(rejected.ErrorCode);
    }

    [Fact]
    public void OnlyACreatedOrStampRejectedInvoiceWithASiigoIdCanBeStamped()
    {
        var pending = NewPending();
        var noId = NewPending();
        noId.ExpireIfStale(Now.AddDays(2));
        noId.ResolveExists("FV-1-5", Now);

        Assert.Equal("siigo.invoice.state_invalid", CodeOf(() => pending.MarkStamped("c", Now)));
        Assert.Equal("siigo.invoice.state_invalid", CodeOf(() => noId.MarkStamped("c", Now)));
        Assert.False(noId.CanStamp);
    }

    [Fact]
    public void AnInvoiceNeverSentCanBeFailed()
    {
        var invoice = NewPending(SiigoInvoiceKind.Freight);

        invoice.MarkFailed("siigo.invoice.order_rejected", "The order invoice was rejected.", Now);

        Assert.Equal(SiigoInvoiceStatus.Failed, invoice.Status);
        Assert.False(invoice.IsLive);
    }

    [Fact]
    public void TheOrderChangedFlagIsKept()
    {
        var invoice = NewPending();

        invoice.MarkOrderChanged();

        Assert.True(invoice.OrderChanged);
    }

    // Spec 2026-11-25 §1: la factura que una persona anuló en Siigo deja de ser vigente y conserva lo que la identifica allá.
    [Fact]
    public void ACreatedInvoiceIsAnnulledKeepingItsSiigoData()
    {
        var invoice = NewPending();
        invoice.MarkCreated("id-1", "FV-1-1", 100m, null, Now);

        invoice.MarkAnnulled(false, Now.AddMinutes(1));

        Assert.Equal(SiigoInvoiceStatus.Annulled, invoice.Status);
        Assert.False(invoice.IsLive);
        Assert.False(invoice.IsOpen);
        Assert.Equal("siigo.invoice.annulled_in_siigo", invoice.ErrorCode);
        Assert.Equal(("id-1", "FV-1-1", 100m), (invoice.SiigoInvoiceId, invoice.Number, invoice.SiigoTotal));
        Assert.False(invoice.HasPdf);
        Assert.False(invoice.CanStamp);
        Assert.Equal(Now.AddMinutes(1), invoice.UpdatedAt);
    }

    [Fact]
    public void ADeletedInvoiceCarriesTheDeletedCode()
    {
        var invoice = NewPending();
        invoice.MarkCreated("id-1", "FV-1-1", 100m, null, Now);

        invoice.MarkAnnulled(true, Now.AddMinutes(1));

        Assert.Equal(SiigoInvoiceStatus.Annulled, invoice.Status);
        Assert.Equal("siigo.invoice.deleted_in_siigo", invoice.ErrorCode);
    }

    [Fact]
    public void AStampedInvoiceIsAnnulledKeepingItsCufe()
    {
        var invoice = NewPending();
        invoice.MarkCreated("id-1", "FV-1-1", 100m, "cufe-abc", Now);

        Assert.True(invoice.IsAnnulmentVerifiable);
        invoice.MarkAnnulled(false, Now.AddMinutes(1));

        Assert.Equal((SiigoInvoiceStatus.Annulled, "cufe-abc"), (invoice.Status, invoice.Cufe));
    }

    [Fact]
    public void AStampRejectedInvoiceIsAnnulledAndLosesTheDianError()
    {
        var invoice = NewPending();
        invoice.MarkCreated("id-1", "FV-1-1", 100m, null, Now);
        invoice.MarkStampRejected("x", "y", Now);

        Assert.True(invoice.IsAnnulmentVerifiable);
        invoice.MarkAnnulled(false, Now.AddMinutes(1));

        Assert.Equal(SiigoInvoiceStatus.Annulled, invoice.Status);
        Assert.Null(invoice.ErrorDetail);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("unknown")]
    [InlineData("rejected")]
    [InlineData("failed")]
    [InlineData("annulled")]
    [InlineData("resolved_without_id")]
    public void OnlyAVerifiableInvoiceCanBeAnnulled(string state)
    {
        var invoice = NewPending();
        switch (state)
        {
            case "unknown":
                invoice.ExpireIfStale(Now.AddDays(2));
                break;
            case "rejected":
                invoice.MarkRejected("x", "y", null, Now);
                break;
            case "failed":
                invoice.MarkFailed("x", "y", Now);
                break;
            case "annulled":
                invoice.MarkCreated("id-1", "FV-1-1", 100m, null, Now);
                invoice.MarkAnnulled(false, Now);
                break;
            case "resolved_without_id":
                invoice.ResolveExists("FV-1-5", Now);
                break;
        }

        var before = invoice.Status;

        Assert.False(invoice.IsAnnulmentVerifiable);
        Assert.Equal("siigo.invoice.state_invalid", CodeOf(() => invoice.MarkAnnulled(false, Now.AddDays(2))));
        Assert.Equal(before, invoice.Status);
    }

    [Fact]
    public void ToStringNeverPrintsTheBody() =>
        Assert.DoesNotContain("\"a\"", NewPending().ToString(), StringComparison.Ordinal);
}
