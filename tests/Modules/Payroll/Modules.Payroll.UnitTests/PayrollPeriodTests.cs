using Modules.Payroll.Domain;

namespace Modules.Payroll.UnitTests;

public sealed class PayrollPeriodTests
{
    private static readonly Guid Tenant = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Company = Guid.Parse("01900000-0000-7000-8000-0000000000c1");
    private static readonly Guid Employee = Guid.Parse("01900000-0000-7000-8000-0000000000e1");
    private static readonly Guid Commissions = Guid.Parse("00000000-0000-0000-0002-000000000001");
    private static readonly PayrollActor Ana = new(Guid.Parse("01900000-0000-7000-8000-0000000000a1"), "Ana Gómez");
    private static readonly PayrollActor Beto = new(Guid.Parse("01900000-0000-7000-8000-0000000000a2"), "Beto Ruiz");
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 15, 0, 0, TimeSpan.Zero);
    private static readonly (DateOnly Start, DateOnly End, PayrollHalf Half) First =
        (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), PayrollHalf.First);

    private static PayrollPeriod Open(PayrollHalf half = PayrollHalf.First) =>
        PayrollPeriod.Open(
            PayrollPeriodId.New(),
            Tenant,
            Company,
            half == PayrollHalf.First ? First : (new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 30), PayrollHalf.Second),
            Ana,
            T0);

    private static PeriodInputDraft Income(decimal amount = 500_000m) =>
        new(Commissions, ConceptKind.Income, null, amount, true);

    private static void Liquidate(PayrollPeriod period, PayrollActor? actor = null, string legal = "{\"a\":1}") =>
        period.RecordLiquidation(
            actor ?? Ana, true, legal, new DateOnly(2026, 11, 1), "2026.11.1", "fp-1", "[]", T0.AddMinutes(1));

    private static PayrollPeriod Liquidated(PayrollActor? actor = null)
    {
        var period = Open();
        Liquidate(period, actor);
        return period;
    }

    private static ApprovalContext Context(
        PayrollActor? caller = null,
        bool dualControl = false,
        bool sourceChanged = false,
        bool negativeNet = false,
        bool firstHalfApproved = true) => new(caller ?? Beto, dualControl, sourceChanged, negativeNet, firstHalfApproved);

    private static string CodeOf(Action action) => Assert.Throws<PayrollDomainException>(action).Code;

    [Fact]
    public void OpeningStartsInDraftAtVersionOne()
    {
        var period = Open();

        Assert.Equal(PayrollPeriodStatus.Draft, period.Status);
        Assert.Equal(1, period.Version);
        Assert.False(period.Outdated);
        Assert.Equal(new DateOnly(2026, 11, 1), period.StartDate);
        Assert.Equal(PayrollHalf.First, period.Half);
        var created = Assert.Single(period.Events);
        Assert.Equal(PeriodAction.Created, created.Action);
        Assert.Null(created.FromStatus);
        Assert.Equal(PayrollPeriodStatus.Draft, created.ToStatus);
        Assert.Equal("Ana Gómez", created.ActorName);
    }

    [Fact]
    public void InputsAreNotEditableOnceApproved()
    {
        var period = Liquidated();
        period.Approve(Context(), T0.AddMinutes(2));

        Assert.Equal(PayrollErrorCodes.PeriodNotEditable, CodeOf(() => period.ReplaceInputs(Employee, [Income()], Ana, T0)));
        Assert.Equal(
            PayrollErrorCodes.PeriodNotEditable,
            CodeOf(() => period.ReplaceInputsBulk(new Dictionary<Guid, IReadOnlyList<PeriodInputDraft>> { [Employee] = [Income()] }, Ana, T0)));

        period.Close(Ana, T0.AddMinutes(3));
        Assert.Equal(PayrollErrorCodes.PeriodNotEditable, CodeOf(() => period.ReplaceInputs(Employee, [Income()], Ana, T0)));
    }

    [Fact]
    public void ReplacingInputsKeepsThePositionOfEachList()
    {
        var period = Open();

        period.ReplaceInputs(
            Employee,
            [
                new PeriodInputDraft(SystemConcepts.All[0].Id, ConceptKind.Overtime, 4.5m, null, null),
                Income(100m),
                Income(200m),
                new PeriodInputDraft(SystemConcepts.All[11].Id, ConceptKind.Deduction, null, 50m, null),
            ],
            Ana,
            T0);
        period.ReplaceInputs(Employee, [Income(300m)], Ana, T0.AddMinutes(1));

        var input = Assert.Single(period.Inputs);
        Assert.Equal(300m, input.Amount);
        Assert.Equal(0, input.Position);
        Assert.Equal(PayrollPeriodStatus.Draft, period.Status);
        // Una captura manual no deja evento en el historial (llamada del plan).
        Assert.Single(period.Events);
    }

    [Fact]
    public void CapturingOnALiquidatedPeriodMarksItOutdatedOnce()
    {
        var period = Liquidated();

        period.ReplaceInputs(Employee, [Income()], Ana, T0.AddMinutes(2));
        period.ReplaceInputs(Employee, [Income(600_000m)], Ana, T0.AddMinutes(3));

        Assert.True(period.Outdated);
        Assert.Single(period.Events, evt => evt.Action == PeriodAction.MarkedOutdated);
    }

    [Fact]
    public void LiquidatingNeedsTheAbsenceConfirmation()
    {
        var period = Open();

        var code = CodeOf(() => period.RecordLiquidation(Ana, false, "{}", new DateOnly(2026, 11, 1), "2026.11.1", "fp", "[]", T0));

        Assert.Equal(PayrollErrorCodes.LiquidationAbsencesUnconfirmed, code);
        Assert.Equal(PayrollPeriodStatus.Draft, period.Status);
    }

    [Fact]
    public void LiquidatingRecordsTheEventAndWhoDidIt()
    {
        var period = Open();

        Liquidate(period, Beto);

        Assert.Equal(PayrollPeriodStatus.Liquidated, period.Status);
        Assert.Equal(Beto.SubjectId, period.LastLiquidatedBy);
        Assert.Equal("Beto Ruiz", period.LastLiquidatedByName);
        Assert.Equal(T0.AddMinutes(1), period.LastLiquidatedAt);
        Assert.Equal("2026.11.1", period.EngineVersion);
        Assert.Equal("fp-1", period.Fingerprint);
        var evt = period.Events[^1];
        Assert.Equal(PeriodAction.Liquidated, evt.Action);
        Assert.Equal(PayrollPeriodStatus.Draft, evt.FromStatus);
        Assert.Equal(PayrollPeriodStatus.Liquidated, evt.ToStatus);
        Assert.True(evt.AbsencesConfirmed);
    }

    [Fact]
    public void TheLegalSnapshotIsFixedOnTheFirstLiquidation()
    {
        var period = Open();
        Liquidate(period, legal: "{\"first\":true}");

        period.RecordLiquidation(Ana, true, "{\"second\":true}", new DateOnly(2026, 12, 1), "2026.11.2", "fp-2", "[]", T0.AddMinutes(5));

        Assert.Equal("{\"first\":true}", period.LegalValuesJson);
        Assert.Equal(new DateOnly(2026, 11, 1), period.LegalValuesDate);
        // Lo demás sí se refresca: la huella y la versión del motor son de la última liquidación.
        Assert.Equal("fp-2", period.Fingerprint);
        Assert.Equal("2026.11.2", period.EngineVersion);
    }

    [Fact]
    public void LiquidatingClearsOutdated()
    {
        var period = Liquidated();
        period.ReplaceInputs(Employee, [Income()], Ana, T0.AddMinutes(2));
        Assert.True(period.Outdated);

        Liquidate(period);

        Assert.False(period.Outdated);
    }

    [Theory]
    [InlineData("approve-in-draft")]
    [InlineData("close-in-liquidated")]
    [InlineData("reopen-in-draft")]
    [InlineData("reopen-in-liquidated")]
    [InlineData("liquidate-in-approved")]
    public void InvalidTransitionsAreRejected(string scenario)
    {
        var code = scenario switch
        {
            "approve-in-draft" => CodeOf(() => Open().Approve(Context(), T0)),
            "close-in-liquidated" => CodeOf(() => Liquidated().Close(Ana, T0)),
            "reopen-in-draft" => CodeOf(() => Open().Reopen(Ana, "Faltó una comisión", T0)),
            "reopen-in-liquidated" => CodeOf(() => Liquidated().Reopen(Ana, "Faltó una comisión", T0)),
            "liquidate-in-approved" => CodeOf(() =>
            {
                var approved = Liquidated();
                approved.Approve(Context(), T0);
                Liquidate(approved);
            }),
            _ => throw new InvalidOperationException(scenario),
        };

        Assert.Equal(PayrollErrorCodes.PeriodInvalidTransition, code);
    }

    [Fact]
    public void ApproveBlockersComeInContractOrder()
    {
        var everything = Open(PayrollHalf.Second);
        var blockers = everything.ApproveBlockers(Context(Ana, dualControl: true, sourceChanged: true, negativeNet: true, firstHalfApproved: false));

        // Un Draft no está desactualizado y nadie lo ha liquidado, así que tampoco hay doble control que cumplir.
        Assert.Equal(["status", "source_changed", "negative_net", "first_half_not_approved"], blockers);

        var outdated = Open(PayrollHalf.Second);
        Liquidate(outdated, Ana);
        outdated.ReplaceInputs(Employee, [Income()], Ana, T0.AddMinutes(2));
        Assert.Equal(
            ["outdated", "source_changed", "negative_net", "first_half_not_approved", "dual_control"],
            outdated.ApproveBlockers(Context(Ana, dualControl: true, sourceChanged: true, negativeNet: true, firstHalfApproved: false)));

        Assert.Empty(Liquidated().ApproveBlockers(Context()));
    }

    [Fact]
    public void EachBlockerIsThrownWithItsOwnCode()
    {
        Assert.Equal(PayrollErrorCodes.PeriodOutdated, CodeOf(() =>
        {
            var period = Liquidated();
            period.ReplaceInputs(Employee, [Income()], Ana, T0.AddMinutes(2));
            period.Approve(Context(), T0.AddMinutes(3));
        }));
        Assert.Equal(PayrollErrorCodes.PeriodOutdated, CodeOf(() => Liquidated().Approve(Context(sourceChanged: true), T0)));
        Assert.Equal(PayrollErrorCodes.PeriodNegativeNet, CodeOf(() => Liquidated().Approve(Context(negativeNet: true), T0)));
        Assert.Equal(
            PayrollErrorCodes.PeriodFirstHalfNotApproved,
            CodeOf(() =>
            {
                var second = Open(PayrollHalf.Second);
                Liquidate(second);
                second.Approve(Context(firstHalfApproved: false), T0);
            }));
        // En una primera quincena «la primera aprobada» no aplica.
        var first = Liquidated();
        first.Approve(Context(firstHalfApproved: false), T0);
        Assert.Equal(PayrollPeriodStatus.Approved, first.Status);
    }

    [Fact]
    public void WithDualControlTheLastLiquidatorCannotApprove()
    {
        var blocked = Liquidated(Ana);
        var code = CodeOf(() => blocked.Approve(Context(Ana, dualControl: true), T0.AddMinutes(2)));
        Assert.Equal(PayrollErrorCodes.PeriodSamePersonCannotApprove, code);

        var other = Liquidated(Ana);
        other.Approve(Context(Beto, dualControl: true), T0.AddMinutes(2));
        Assert.Equal(Beto.SubjectId, other.ApprovedBy);

        // Sin doble control, quien liquidó sí aprueba.
        var allowed = Liquidated(Ana);
        allowed.Approve(Context(Ana, dualControl: false), T0.AddMinutes(2));
        Assert.Equal(PayrollPeriodStatus.Approved, allowed.Status);
    }

    [Fact]
    public void AReopenedPeriodRemembersTheReason()
    {
        var period = Liquidated();
        period.Approve(Context(), T0.AddMinutes(2));

        period.Reopen(Beto, "Faltó una comisión", T0.AddMinutes(3));

        Assert.Equal(PayrollPeriodStatus.Reopened, period.Status);
        var evt = period.Events[^1];
        Assert.Equal(PeriodAction.Reopened, evt.Action);
        Assert.Equal("Faltó una comisión", evt.Reason);
        Assert.Equal(PayrollPeriodStatus.Approved, evt.FromStatus);
        Assert.Equal(PayrollPeriodStatus.Reopened, evt.ToStatus);
        Assert.Null(period.ApprovedAt);
        // Reabrir un Closed también vale.
        var closed = Liquidated();
        closed.Approve(Context(), T0.AddMinutes(2));
        closed.Close(Ana, T0.AddMinutes(3));
        closed.Reopen(Ana, "Otra razón", T0.AddMinutes(4));
        Assert.Equal(PayrollPeriodStatus.Reopened, closed.Status);
    }

    [Fact]
    public void ReopeningClearsTheApprovedAndClosedMarksButKeepsTheLastLiquidator()
    {
        var period = Liquidated(Ana);
        period.Approve(Context(Beto, dualControl: true), T0.AddMinutes(2));
        period.Close(Beto, T0.AddMinutes(3));
        Assert.NotNull(period.ApprovedBy);
        Assert.NotNull(period.ClosedBy);

        period.Reopen(Ana, "reason", T0.AddMinutes(4));

        Assert.Null(period.ApprovedBy);
        Assert.Null(period.ApprovedByName);
        Assert.Null(period.ApprovedAt);
        Assert.Null(period.ClosedBy);
        Assert.Null(period.ClosedByName);
        Assert.Null(period.ClosedAt);
        // Dual control keeps working after a reopen: the last liquidator survives.
        Assert.Equal(Ana.SubjectId, period.LastLiquidatedBy);
    }
    [Fact]
    public void ClosingAnOutdatedApprovedIsRejected()
    {
        var period = Liquidated();
        period.Approve(Context(), T0.AddMinutes(2));
        period.MarkOutdated(Ana, T0.AddMinutes(3));

        Assert.True(period.Outdated);
        Assert.Equal(PayrollErrorCodes.PeriodOutdated, CodeOf(() => period.Close(Ana, T0.AddMinutes(4))));
    }

    [Fact]
    public void MarkingOutdatedNeedsALiquidatedPeriodAndHappensOnce()
    {
        var draft = Open();
        draft.MarkOutdated(Ana, T0);
        Assert.False(draft.Outdated);
        Assert.Equal(1, draft.Version);

        var period = Liquidated();
        period.MarkOutdated(Ana, T0.AddMinutes(2));
        var version = period.Version;
        period.MarkOutdated(Ana, T0.AddMinutes(3));

        Assert.Equal(version, period.Version);
        Assert.Single(period.Events, evt => evt.Action == PeriodAction.MarkedOutdated);
    }

    [Fact]
    public void EveryMutationBumpsTheVersion()
    {
        var period = Open();
        var version = period.Version;

        void Bumped(Action action)
        {
            action();
            Assert.Equal(version + 1, period.Version);
            version = period.Version;
        }

        Bumped(() => period.ReplaceInputs(Employee, [Income()], Ana, T0));
        Bumped(() => period.ReplaceInputsBulk(new Dictionary<Guid, IReadOnlyList<PeriodInputDraft>> { [Employee] = [Income(2m)] }, Ana, T0));
        Bumped(() => Liquidate(period));
        // Capturar sobre un liquidado marca desactualizado y sube la versión una sola vez.
        Bumped(() => period.ReplaceInputs(Employee, [Income(3m)], Ana, T0));
        Bumped(() => Liquidate(period));
        Bumped(() => period.Approve(Context(), T0));
        Bumped(() => period.Close(Ana, T0));
        Bumped(() => period.Reopen(Ana, "motivo", T0));
        Assert.Equal(PayrollPeriodStatus.Reopened, period.Status);
    }

    [Fact]
    public void ABulkImportLeavesItsEventAndReplacesOnlyTheNamedEmployees()
    {
        var period = Open();
        var other = Guid.NewGuid();
        period.ReplaceInputs(other, [Income(1m)], Ana, T0);

        period.ReplaceInputsBulk(
            new Dictionary<Guid, IReadOnlyList<PeriodInputDraft>> { [Employee] = [Income(7m), Income(8m)] }, Ana, T0.AddMinutes(1));

        Assert.Equal(3, period.Inputs.Count);
        Assert.Equal([0, 1], period.Inputs.Where(input => input.EmployeeId == Employee).Select(input => input.Position).Order());
        Assert.Equal(PeriodAction.InputsImported, period.Events[^1].Action);
    }

    // Review M-3: two events written in one operation share OccurredAt and Guid v7 is not monotonic inside a millisecond, so the
    // history order needs its own sequence.
    [Fact]
    public void EventsCarryAStrictlyIncreasingSequenceEvenWithTheSameInstant()
    {
        var period = Liquidated();
        period.ReplaceInputsBulk(
            new Dictionary<Guid, IReadOnlyList<PeriodInputDraft>> { [Employee] = [Income()] }, Ana, T0.AddMinutes(5));

        Assert.Equal(
            [PeriodAction.Created, PeriodAction.Liquidated, PeriodAction.InputsImported, PeriodAction.MarkedOutdated],
            period.Events.Select(evt => evt.Action));
        Assert.Equal([1, 2, 3, 4], period.Events.Select(evt => evt.Sequence));
        Assert.Equal(period.Events[2].OccurredAt, period.Events[3].OccurredAt);
    }
}
