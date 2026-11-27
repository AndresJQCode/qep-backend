using Modules.Wms.Domain;
using Modules.Wms.Domain.Control;
using Modules.Wms.Domain.Inventory;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.UnitTests.Control;

public sealed class IncidentLifecycleTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Reporter = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Supervisor = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static Incident Report(IncidentType type = IncidentType.SyncConflict) =>
        Incident.Report(
            Tenant,
            "NOV-20261126-0001",
            WarehouseId.New(),
            OwnerId.New(),
            type,
            IncidentSubject.Empty,
            null,
            null,
            null,
            [],
            Reporter,
            "zebra-01",
            "key-1",
            Now,
            Now);

    [Fact]
    public void ReviewMovesOpenToInReviewAndBumpsTheVersion()
    {
        var incident = Report();

        incident.Review(Supervisor, Now.AddMinutes(5));

        Assert.Equal((IncidentStatus.InReview, 2L), (incident.Status, incident.Version));
        var last = incident.Events[^1];
        Assert.Equal((IncidentEventKind.InReview, Supervisor), (last.Kind, last.MemberId));
        Assert.Equal(Now.AddMinutes(5), last.RecordedAt);
    }

    [Fact]
    public void ReviewingTwiceIsAnInvalidTransition()
    {
        var incident = Report();
        incident.Review(Supervisor, Now);

        var error = Assert.Throws<InvalidTransitionException>(() => incident.Review(Supervisor, Now));

        Assert.Equal("InReview", error.ProblemExtensions["from"]);
        Assert.Equal("review", error.ProblemExtensions["action"]);
    }

    [Theory]
    [InlineData(IncidentType.ShortMove, IncidentResolution.AcceptIntoStock)]
    [InlineData(IncidentType.CountVariance, IncidentResolution.Accept)]
    [InlineData(IncidentType.SyncConflict, IncidentResolution.Accept)]
    public void ResolvingWithAResolutionTheTypeDoesNotAllowIs422(IncidentType type, IncidentResolution resolution)
    {
        var incident = Report(type);

        var error = Assert.Throws<WmsDomainException>(() => incident.Resolve(resolution, Supervisor, null, null, Now));

        Assert.Equal(WmsErrorCodes.IncidentResolutionNotAllowed, error.Code);
        Assert.Equal((IncidentStatus.Open, 1L), (incident.Status, incident.Version));
    }

    [Fact]
    public void ResolvingIsTerminal()
    {
        var incident = Report(IncidentType.UnreadableLabel);
        incident.Resolve(IncidentResolution.Accept, Supervisor, null, null, Now);

        Assert.Throws<InvalidTransitionException>(() => incident.Resolve(IncidentResolution.Accept, Supervisor, null, null, Now));
        Assert.Throws<InvalidTransitionException>(() => incident.Reject(Supervisor, "no", Now));
        Assert.Throws<InvalidTransitionException>(() => incident.Review(Supervisor, Now));
    }

    [Fact]
    public void RejectedIsTerminalToo()
    {
        var incident = Report();
        incident.Reject(Supervisor, "duplicada", Now);

        Assert.Throws<InvalidTransitionException>(() => incident.Reject(Supervisor, "otra vez", Now));
        Assert.Throws<InvalidTransitionException>(() => incident.Resolve(IncidentResolution.AcceptIntoStock, Supervisor, null, null, Now));
    }

    [Fact]
    public void ResolveWorksFromInReview()
    {
        var incident = Report(IncidentType.LocationMismatch);
        incident.Review(Supervisor, Now);

        incident.Resolve(IncidentResolution.Accept, Supervisor, null, null, Now);

        Assert.Equal((IncidentStatus.Resolved, 3L), (incident.Status, incident.Version));
    }

    [Fact]
    public void LinkingAVerificationKeepsTheVersionAndLeavesAnEventForTheSystem()
    {
        var incident = Report(IncidentType.ShortMove);
        var count = Guid.CreateVersion7();

        incident.LinkVerification(count, Now, "TSK-20261126-0001");

        Assert.Equal(count, incident.VerificationTaskId);
        Assert.Equal((IncidentStatus.Open, 1L), (incident.Status, incident.Version));
        var last = incident.Events[^1];
        Assert.Equal((IncidentEventKind.VerificationLinked, (Guid?)null, "TSK-20261126-0001"), (last.Kind, last.MemberId, last.Detail));
    }

    [Fact]
    public void ASystemResolutionHasNoMemberAndCanPointToAParent()
    {
        var incident = Report(IncidentType.ShortMove);
        var parent = Guid.CreateVersion7();

        incident.ResolveBySystem(IncidentResolution.Accept, "Verificada por TSK-1: ver NOV-2.", parent, Now.AddMinutes(3));

        Assert.Equal((IncidentStatus.Resolved, IncidentResolution.Accept, Guid.Empty, 2L), (incident.Status, incident.Resolution, incident.ResolvedBy, incident.Version));
        Assert.Equal((parent, Now.AddMinutes(3), "Verificada por TSK-1: ver NOV-2."), (incident.ParentIncidentId, incident.ResolvedAt, incident.ResolutionNote));
        var last = incident.Events[^1];
        Assert.Equal((IncidentEventKind.Resolved, (Guid?)null, "Accept"), (last.Kind, last.MemberId, last.Detail));
    }

    [Fact]
    public void ASystemResolutionStillObeysTheCatalogAndTheState()
    {
        var variance = Report(IncidentType.CountVariance);
        Assert.Equal(
            WmsErrorCodes.IncidentResolutionNotAllowed,
            Assert.Throws<WmsDomainException>(() => variance.ResolveBySystem(IncidentResolution.Accept, "x", null, Now)).Code);

        var rejected = Report();
        rejected.Reject(Supervisor, "descartada", Now);
        Assert.Throws<InvalidTransitionException>(() => rejected.ResolveBySystem(IncidentResolution.AcceptIntoStock, "x", null, Now));
    }

    [Fact]
    public void UnlinkingKeepsTheIncidentOpenAndBumpsTheVersion()
    {
        var incident = Report(IncidentType.ShortMove);
        incident.LinkVerification(Guid.CreateVersion7(), Now, "TSK-1");

        incident.UnlinkVerification("TSK-1", Now.AddMinutes(4));

        Assert.Null(incident.VerificationTaskId);
        Assert.Equal((IncidentStatus.Open, 2L), (incident.Status, incident.Version));
        var last = incident.Events[^1];
        Assert.Equal((IncidentEventKind.VerificationCancelled, (Guid?)null, "TSK-1"), (last.Kind, last.MemberId, last.Detail));
    }

    [Fact]
    public void AttachingAnAdjustmentOnlyFitsACountVariance()
    {
        var adjustment = Guid.CreateVersion7();
        var variance = Report(IncidentType.CountVariance);

        variance.AttachAdjustment(adjustment);

        Assert.Equal(adjustment, variance.AdjustmentId);
        Assert.Throws<InvalidOperationException>(() => Report(IncidentType.ShortMove).AttachAdjustment(adjustment));
    }

    [Fact]
    public void ALinkOnAClosedIncidentIsAnInvalidTransition()
    {
        var incident = Report();
        incident.Reject(Supervisor, "descartada", Now);

        var error = Assert.Throws<InvalidTransitionException>(() => incident.LinkVerification(Guid.NewGuid(), Now));

        Assert.Equal("link-verification", error.ProblemExtensions["action"]);
        Assert.Null(incident.VerificationTaskId);
    }

    [Fact]
    public void OnlyAnIncidentThatMovedNothingCanBeRejected()
    {
        var conflict = Report();
        conflict.Reject(Supervisor, "descartada", Now);
        Assert.Equal(IncidentStatus.Rejected, conflict.Status);

        var shortMove = Report(IncidentType.ShortMove);
        var error = Assert.Throws<WmsDomainException>(() => shortMove.Reject(Supervisor, "no", Now));
        Assert.Equal(WmsErrorCodes.IncidentResolutionNotAllowed, error.Code);
        Assert.Equal(IncidentStatus.Open, shortMove.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void RejectingNeedsANote(string? note)
    {
        var incident = Report();

        Assert.Equal(WmsErrorCodes.IncidentInvalid, Assert.Throws<WmsDomainException>(() => incident.Reject(Supervisor, note!, Now)).Code);
    }

    [Fact]
    public void RejectingNeedsANoteOfAtMostFiveHundred()
    {
        var incident = Report();

        Assert.Equal(
            WmsErrorCodes.IncidentInvalid,
            Assert.Throws<WmsDomainException>(() => incident.Reject(Supervisor, new string('a', 501), Now)).Code);
        incident.Reject(Supervisor, new string('a', 500), Now);
        Assert.Equal(IncidentStatus.Rejected, incident.Status);
    }

    [Fact]
    public void TheReporterCannotAcceptIntoStockTheirOwnConflict()
    {
        var incident = Report();

        var error = Assert.Throws<WmsDomainException>(() => incident.EnsureNotSelfResolution(IncidentResolution.AcceptIntoStock, Reporter));

        Assert.Equal(WmsErrorCodes.IncidentSelfResolution, error.Code);
        incident.EnsureNotSelfResolution(IncidentResolution.AcceptIntoStock, Supervisor);
        incident.EnsureNotSelfResolution(IncidentResolution.Accept, Reporter);
    }

    [Fact]
    public void ResolvingRecordsWhoWhenAndTheBatch()
    {
        var incident = Report();
        var batch = Guid.NewGuid();
        var at = Now.AddMinutes(9);

        incident.Resolve(IncidentResolution.AcceptIntoStock, Supervisor, "  ok  ", batch, at);

        Assert.Equal(IncidentResolution.AcceptIntoStock, incident.Resolution);
        Assert.Equal((Supervisor, at, "ok", batch), (incident.ResolvedBy, incident.ResolvedAt, incident.ResolutionNote, incident.BatchId));
        var last = incident.Events[^1];
        Assert.Equal((IncidentEventKind.Resolved, Supervisor), (last.Kind, last.MemberId));
    }

    [Fact]
    public void RejectingRecordsWhoWhenAndTheNote()
    {
        var incident = Report();

        incident.Reject(Supervisor, " duplicada ", Now.AddMinutes(2));

        Assert.Equal((Supervisor, Now.AddMinutes(2), "duplicada"), (incident.ResolvedBy, incident.ResolvedAt, incident.ResolutionNote));
        Assert.Null(incident.Resolution);
        Assert.Equal(IncidentEventKind.Rejected, incident.Events[^1].Kind);
    }

    [Fact]
    public void EnsureCanResolveAnswersLikeResolveAndChangesNothing()
    {
        var conflict = Report();
        conflict.EnsureCanResolve(IncidentResolution.AcceptIntoStock);
        Assert.Equal((IncidentStatus.Open, 1L), (conflict.Status, conflict.Version));

        Assert.Equal(
            WmsErrorCodes.IncidentResolutionNotAllowed,
            Assert.Throws<WmsDomainException>(() => conflict.EnsureCanResolve(IncidentResolution.Accept)).Code);
        Assert.Equal(
            WmsErrorCodes.IncidentResolutionNotAllowed,
            Assert.Throws<WmsDomainException>(() => Report(IncidentType.CountVariance).EnsureCanResolve(IncidentResolution.Accept)).Code);
        conflict.Reject(Supervisor, "duplicada", Now);
        Assert.Throws<InvalidTransitionException>(() => conflict.EnsureCanResolve(IncidentResolution.AcceptIntoStock));
    }
}
