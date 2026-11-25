using System.Globalization;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>
/// La generación con cambio programado, retención y «sin revisar» (spec reajuste §6.3 a §6.5), sobre un contrato mensual
/// anclado el día 10.
/// </summary>
public sealed class ChargeGeneratorReviewTests
{
    private const decimal OldPrice = 100000m;

    private const decimal NewPrice = 107200m;

    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static DateOnly Date(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ServiceContract NewService(
        AdjustmentMode mode = AdjustmentMode.Ipc, string review = "2027-01-10", string lastGenerated = "2026-12-10")
    {
        var service = ServiceContract.Create(
            ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", OldPrice, "COP",
            BillingCycle.Monthly, Date("2026-01-10"), Now);
        service.MarkGenerated(Date(lastGenerated));
        service.InitializePriceReview(mode, 0m, Date(review));
        return service;
    }

    private static ServicePriceSchedule ConfirmIpc(ServiceContract service) =>
        ServicePriceSchedule.ConfirmIpc(service, 2026, 7.2m, NewPrice, "IPC 2026 (7,20 %)", Guid.NewGuid(), Now);

    private static GenerationResult Run(ServiceContract service, string today, ServicePriceSchedule? pending = null) =>
        ChargeGenerator.Generate(service, pending, GenerationMode.Run, Date(today), Now);

    private static (DateOnly Period, DateOnly Due)[] Dates(GenerationResult result) =>
        result.Charges.Select(charge => (charge.PeriodDate, charge.DueDate)).ToArray();

    [Fact]
    public void AnIpcReviewInTheWindowHoldsAndGeneratesNothingFromThere()
    {
        var service = NewService(lastGenerated: "2026-11-10");

        var result = Run(service, "2027-02-10");

        Assert.Equal([(Date("2026-12-10"), Date("2026-12-10"))], Dates(result));
        Assert.Equal(Date("2027-02-10"), service.HeldSince);
        Assert.Equal(Date("2026-12-10"), service.LastGeneratedPeriodDate);
        Assert.Equal(Date("2027-01-10"), service.NextReviewDate);
        Assert.Empty(result.PriceChanges);
        Assert.True(result.ServiceChanged);
    }

    [Fact]
    public void ANewHoldThatGeneratesNothingStillChangesTheService()
    {
        var service = NewService();

        var result = Run(service, "2027-01-05");

        Assert.Empty(result.Charges);
        Assert.Equal(Date("2027-01-05"), service.HeldSince);
        Assert.True(result.ServiceChanged);
    }

    [Fact]
    public void AHeldContractKeepsItsHoldSinceWhileStillHeld()
    {
        var service = NewService();
        Run(service, "2027-01-05");

        var result = Run(service, "2027-01-08");

        Assert.Empty(result.Charges);
        Assert.Equal(Date("2027-01-05"), service.HeldSince);
        Assert.False(result.ServiceChanged);
    }

    [Fact]
    public void ReleasingWithACoveringScheduleShiftsTheDueDate()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        var schedule = ConfirmIpc(service);

        var result = Run(service, "2027-01-14", schedule);

        Assert.Equal([(Date("2027-01-10"), Date("2027-01-19"))], Dates(result));
        Assert.Equal(NewPrice, result.Charges[0].Amount);
        Assert.False(result.Charges[0].PriceUnreviewed);
        Assert.Equal(NewPrice, service.Price);
        Assert.Null(service.HeldSince);
        Assert.Equal(PriceScheduleStatus.Applied, schedule.Status);
        Assert.Equal(Date("2027-01-10"), schedule.AppliedPeriodDate);
        Assert.Equal(Now, schedule.AppliedAt);
        Assert.Equal(Date("2028-01-10"), service.NextReviewDate);
        var change = Assert.Single(result.PriceChanges);
        Assert.Equal(PriceChangeOrigin.Ipc, change.Origin);
        Assert.Equal(schedule.Id, change.ScheduleId);
        Assert.Equal(OldPrice, change.PreviousPrice);
        Assert.Equal(NewPrice, change.NewPrice);
        Assert.Equal("IPC 2026 (7,20 %)", change.Reason);
        Assert.Equal(schedule.CreatedByMemberId, change.ChangedByMemberId);

        // El siguiente período sigue en su ancla.
        Assert.Equal([(Date("2027-02-10"), Date("2027-02-10"))], Dates(Run(service, "2027-02-05")));
    }

    [Fact]
    public void SeveralHeldPeriodsAreReleasedTogetherWithTheSameDueDate()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        var schedule = ConfirmIpc(service);

        var result = Run(service, "2027-03-20", schedule);

        Assert.Equal(
            [
                (Date("2027-01-10"), Date("2027-03-25")),
                (Date("2027-02-10"), Date("2027-03-25")),
                (Date("2027-03-10"), Date("2027-03-25")),
            ],
            Dates(result));
        Assert.All(result.Charges, charge => Assert.Equal(NewPrice, charge.Amount));
        Assert.Single(result.PriceChanges);
        Assert.Null(service.HeldSince);
    }

    [Fact]
    public void AReleaseThatReachesAnotherReviewHoldsAgainAsANewHold()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        var schedule = ConfirmIpc(service);

        var result = Run(service, "2028-01-07", schedule);

        Assert.Equal(12, result.Charges.Count);
        Assert.All(result.Charges, charge => Assert.Equal(Date("2028-01-12"), charge.DueDate));
        Assert.Equal(Date("2027-12-10"), result.Charges[^1].PeriodDate);
        Assert.Equal(Date("2028-01-07"), service.HeldSince);
        Assert.Equal(Date("2028-01-10"), service.NextReviewDate);
    }

    [Fact]
    public void AManualReviewGeneratesUnreviewedAndAdvances()
    {
        var service = NewService(AdjustmentMode.Manual);

        var result = Run(service, "2027-02-05");

        Assert.Equal(
            [(Date("2027-01-10"), true), (Date("2027-02-10"), false)],
            result.Charges.Select(charge => (charge.PeriodDate, charge.PriceUnreviewed)));
        Assert.All(result.Charges, charge => Assert.Equal(charge.PeriodDate, charge.DueDate));
        Assert.All(result.Charges, charge => Assert.Equal(OldPrice, charge.Amount));
        Assert.Equal(Date("2028-01-10"), service.NextReviewDate);
        Assert.Null(service.HeldSince);
        Assert.True(result.ServiceChanged);
    }

    [Fact]
    public void AScheduleBeforeTheReviewAppliesWithoutResolvingIt()
    {
        var service = NewService(review: "2027-03-10");
        var schedule = ServicePriceSchedule.Schedule(service, null, NewPrice, Date("2027-01-10"), "Más usuarios", null, Now);

        var applied = Run(service, "2027-01-05", schedule);

        Assert.Equal(NewPrice, Assert.Single(applied.Charges).Amount);
        Assert.Equal(PriceChangeOrigin.Scheduled, Assert.Single(applied.PriceChanges).Origin);
        Assert.Equal(Date("2027-03-10"), service.NextReviewDate);

        var atTheReview = Run(service, "2027-03-05", schedule);

        Assert.Equal([(Date("2027-02-10"), Date("2027-02-10"))], Dates(atTheReview));
        Assert.Empty(atTheReview.PriceChanges);
        Assert.Equal(Date("2027-03-05"), service.HeldSince);
    }

    [Fact]
    public void AScheduleThatIsNotReachedYetStaysPending()
    {
        var service = NewService(AdjustmentMode.Manual, lastGenerated: "2026-10-10");
        var schedule = ServicePriceSchedule.Schedule(service, null, NewPrice, Date("2027-01-10"), "Más usuarios", null, Now);

        var result = Run(service, "2026-11-05", schedule);

        Assert.Equal(OldPrice, Assert.Single(result.Charges).Amount);
        Assert.Empty(result.PriceChanges);
        Assert.Equal(PriceScheduleStatus.Pending, schedule.Status);
    }

    [Theory]
    [InlineData(AdjustmentMode.Ipc)]
    [InlineData(AdjustmentMode.Manual)]
    public void CreationNeverHoldsNorMarksUnreviewed(AdjustmentMode mode)
    {
        var service = ServiceContract.Create(
            ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", OldPrice, "COP",
            BillingCycle.Monthly, Date("2025-12-10"), Now);
        service.InitializePriceReview(mode, 0m, Date("2026-12-10"));

        var result = ChargeGenerator.Generate(service, null, GenerationMode.Creation, Date("2027-01-05"), Now);

        Assert.Equal(14, result.Charges.Count);
        Assert.All(result.Charges, charge => Assert.False(charge.PriceUnreviewed));
        Assert.All(result.Charges, charge => Assert.Equal(charge.PeriodDate, charge.DueDate));
        Assert.Null(service.HeldSince);
        Assert.Equal(Date("2026-12-10"), service.NextReviewDate);
    }

    [Fact]
    public void PausingAHeldContractClearsTheHold()
    {
        var service = NewService();
        Run(service, "2027-01-05");

        service.Pause(Date("2027-01-08"), Now);
        var result = Run(service, "2027-01-08");

        Assert.Null(service.HeldSince);
        Assert.Equal([Date("2027-01-10")], service.CarriedHeldPeriods);
        Assert.Empty(result.Charges);
        Assert.False(result.ServiceChanged);
    }

    // Spec reajuste §6.4: pausar un retenido conserva los períodos cuyo día de generación ya llegó, incluido el de la pausa.
    [Theory]
    [InlineData("2027-02-05", new[] { "2027-01-10", "2027-02-10" })]
    [InlineData("2027-02-04", new[] { "2027-01-10" })]
    public void PausingAHeldContractKeepsThePeriodsWhoseGenerationDayHadCome(string pausedOn, string[] carried)
    {
        var service = NewService();
        Run(service, "2027-01-05");

        service.Pause(Date(pausedOn), Now);

        Assert.Equal(carried.Select(Date), service.CarriedHeldPeriods);
        Assert.Null(service.HeldSince);
    }

    // Sin retención al pausar no se arrastra nada, aunque el período de revisión entre en la ventana ese mismo día.
    [Fact]
    public void PausingAContractThatWasNotHeldCarriesNothing()
    {
        var service = NewService();
        Assert.Empty(Run(service, "2027-01-04").Charges);
        Assert.Null(service.HeldSince);

        service.Pause(Date("2027-01-05"), Now);
        Assert.Empty(service.CarriedHeldPeriods);
        service.Resume(Date("2027-03-01"), Now);
        var result = Run(service, "2027-03-05");

        Assert.Empty(result.Charges);
        Assert.Equal(Date("2027-03-05"), service.HeldSince);
        // Enero y febrero cayeron en la pausa: nunca se cobran.
        Assert.Equal(Date("2027-03-10"), PriceReview.ReviewPeriodOf(service));
    }

    [Fact]
    public void ResumingKeepsTheCarriedPeriodsHeld()
    {
        var service = ResumedHeldService();

        Assert.Equal(Date("2027-04-20"), service.HeldSince);
        var review = PriceReview.Of(service, null, Date("2027-04-20"));
        Assert.Equal(
            (ReviewState.Held, (DateOnly?)Date("2027-01-10"), (int?)2026), (review.State, review.ReviewPeriodDate, review.IpcYear));
        Assert.Equal([Date("2027-01-10")], service.UpcomingPeriods());

        var result = Run(service, "2027-04-21");

        Assert.Empty(result.Charges);
        Assert.False(result.ServiceChanged);
        Assert.Equal(Date("2027-04-20"), service.HeldSince);
    }

    [Fact]
    public void ReleasingTheCarriedPeriodsBillsThemAndSkipsThePause()
    {
        var service = ResumedHeldService();

        var result = Run(service, "2027-04-21", ConfirmIpc(service));

        Assert.Equal(
            [(Date("2027-01-10"), Date("2027-04-26")), (Date("2027-02-10"), Date("2027-04-26"))],
            Dates(result));
        Assert.All(result.Charges, charge => Assert.Equal(NewPrice, charge.Amount));
        Assert.Empty(service.CarriedHeldPeriods);
        Assert.Null(service.HeldSince);
        Assert.Equal(Date("2028-01-10"), service.NextReviewDate);
        // Marzo y abril cayeron en la pausa.
        Assert.Equal(Date("2027-05-10"), service.NextPeriodToGenerate());
        Assert.Equal([(Date("2027-05-10"), Date("2027-05-10"))], Dates(Run(service, "2027-05-05")));
    }

    [Fact]
    public void SeveralPausesKeepEveryHeldGap()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        service.Pause(Date("2027-01-20"), Now);
        Assert.Equal([Date("2027-01-10")], service.CarriedHeldPeriods);
        service.Resume(Date("2027-03-01"), Now);
        Assert.Empty(Run(service, "2027-03-06").Charges);
        service.Pause(Date("2027-03-07"), Now);
        Assert.Equal([Date("2027-01-10"), Date("2027-03-10")], service.CarriedHeldPeriods);
        service.Resume(Date("2027-06-01"), Now);

        var result = Run(service, "2027-06-02", ConfirmIpc(service));

        Assert.Equal(
            [(Date("2027-01-10"), Date("2027-06-07")), (Date("2027-03-10"), Date("2027-06-07"))],
            Dates(result));
        // Febrero, abril y mayo cayeron en una pausa.
        Assert.Equal(Date("2027-06-10"), service.NextPeriodToGenerate());
    }

    [Fact]
    public void SwitchingToManualWhilePausedBillsTheCarriedPeriodsAtResume()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        service.Pause(Date("2027-02-05"), Now);
        service.UpdatePriceReview(AdjustmentMode.Manual, 0m, Date("2027-01-10"), Now);
        service.Resume(Date("2027-04-20"), Now);

        var result = Run(service, "2027-04-20");

        Assert.Equal(
            [(Date("2027-01-10"), Date("2027-04-25"), true), (Date("2027-02-10"), Date("2027-04-25"), false)],
            result.Charges.Select(charge => (charge.PeriodDate, charge.DueDate, charge.PriceUnreviewed)));
        Assert.All(result.Charges, charge => Assert.Equal(OldPrice, charge.Amount));
        Assert.Equal(Date("2028-01-10"), service.NextReviewDate);
        Assert.Null(service.HeldSince);
    }

    // Lo arrastrado conserva su revisión (con el IPC de su año) y lo posterior a la pausa tiene la suya: nunca se componen.
    [Fact]
    public void AfterALongPauseTheCarriedReviewAndTheNewYearAreTwoReviews()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        service.Pause(Date("2027-01-20"), Now);
        service.Resume(Date("2030-05-03"), Now);
        Assert.Equal(2026, PriceReview.Of(service, null, Date("2030-05-03")).IpcYear);

        var released = Run(service, "2030-05-06", ConfirmIpc(service));

        Assert.Equal([(Date("2027-01-10"), Date("2030-05-11"))], Dates(released));
        Assert.Equal(Date("2030-05-06"), service.HeldSince);
        var review = PriceReview.Of(service, null, Date("2030-05-06"));
        Assert.Equal(((DateOnly?)Date("2030-05-10"), (int?)2029), (review.ReviewPeriodDate, review.IpcYear));

        var second = ServicePriceSchedule.ConfirmIpc(service, 2029, 7.2m, NewPrice, "IPC 2029 (7,20 %)", null, Now);
        var result = Run(service, "2030-05-06", second);

        Assert.Equal([(Date("2030-05-10"), Date("2030-05-11"))], Dates(result));
        Assert.Equal(Date("2031-01-10"), service.NextReviewDate);
        Assert.Null(service.HeldSince);
    }

    [Fact]
    public void CancellingDropsTheCarriedPeriods()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        service.Pause(Date("2027-02-05"), Now);

        service.Cancel(Now);

        Assert.Empty(service.CarriedHeldPeriods);
        Assert.Null(service.HeldSince);
    }

    /// <summary>Retenido el 5 de enero de 2027, pausado el 5 de febrero y reanudado el 20 de abril.</summary>
    private static ServiceContract ResumedHeldService()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        service.Pause(Date("2027-02-05"), Now);
        service.Resume(Date("2027-04-20"), Now);
        return service;
    }

    // Una sola revisión, no una por cada año saltado, y con el IPC de un solo año.
    [Fact]
    public void AfterAPauseOfSeveralYearsThereIsOneReview()
    {
        var service = NewService(lastGenerated: "2026-11-10");
        service.Pause(Date("2026-11-05"), Now);
        service.Resume(Date("2030-05-03"), Now);
        Assert.Equal(2029, PriceReview.Of(service, null, Date("2030-05-03")).IpcYear);

        Assert.Empty(Run(service, "2030-05-06").Charges);
        var schedule = ServicePriceSchedule.ConfirmIpc(service, 2029, 7.2m, NewPrice, "IPC 2029 (7,20 %)", null, Now);
        var result = Run(service, "2030-05-06", schedule);

        Assert.Equal([(Date("2030-05-10"), Date("2030-05-11"))], Dates(result));
        Assert.Single(result.PriceChanges);
        Assert.Equal(Date("2031-01-10"), service.NextReviewDate);
        Assert.Null(service.HeldSince);
    }

    // Spec reajuste §6.4: pasar a Manual un retenido libera por «sin revisar», con el vencimiento corrido.
    [Fact]
    public void SwitchingAHeldContractToManualReleasesItUnreviewed()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        service.UpdatePriceReview(AdjustmentMode.Manual, 0m, service.NextReviewDate, Now);

        var result = Run(service, "2027-01-14");

        var charge = Assert.Single(result.Charges);
        Assert.Equal((Date("2027-01-10"), Date("2027-01-19"), true), (charge.PeriodDate, charge.DueDate, charge.PriceUnreviewed));
        Assert.Equal(OldPrice, charge.Amount);
        Assert.Equal(Date("2028-01-10"), service.NextReviewDate);
        Assert.Null(service.HeldSince);
    }

    // Spec reajuste §6.4: correr la revisión más allá del período retenido es una decisión de una persona: no va «sin revisar».
    [Fact]
    public void MovingTheReviewPastTheHeldPeriodReleasesItAsReviewed()
    {
        var service = NewService();
        Run(service, "2027-01-05");
        service.UpdatePriceReview(AdjustmentMode.Ipc, 0m, Date("2027-06-10"), Now);

        var result = Run(service, "2027-01-14");

        var charge = Assert.Single(result.Charges);
        Assert.Equal((Date("2027-01-10"), Date("2027-01-19"), false), (charge.PeriodDate, charge.DueDate, charge.PriceUnreviewed));
        Assert.Equal(OldPrice, charge.Amount);
        Assert.Equal(Date("2027-06-10"), service.NextReviewDate);
        Assert.Null(service.HeldSince);
    }

    [Fact]
    public void ANotHeldPassDueOnTheAnchor()
    {
        var service = NewService(review: "2028-01-10", lastGenerated: "2026-10-10");

        var result = Run(service, "2027-01-05");

        Assert.Equal(3, result.Charges.Count);
        Assert.All(result.Charges, charge => Assert.Equal(charge.PeriodDate, charge.DueDate));
    }

    [Fact]
    public void NothingToDoChangesNothing()
    {
        var result = Run(NewService(), "2026-12-12");

        Assert.Empty(result.Charges);
        Assert.False(result.ServiceChanged);
    }
}
