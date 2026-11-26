using System.Globalization;
using BuildingBlocks.Domain.Pricing;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>La revisión de precio (spec reajuste §5 y §6.1): una sola función pura que usan los DTO, las acciones y la corrida.</summary>
public sealed class PriceReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static DateOnly Date(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Mensual anclado el día 10: sin generar nada, la revisión cae en el período que diga `review`.
    private static ServiceContract NewService(AdjustmentMode mode = AdjustmentMode.Ipc, string review = "2027-01-10", string lastGenerated = "2026-12-10")
    {
        var service = ServiceContract.Create(
            ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", 100000m, "COP",
            BillingCycle.Monthly, Date("2026-01-10"), Now);
        service.MarkGenerated(Date(lastGenerated));
        service.InitializePriceReview(mode, 0m, Date(review));
        return service;
    }

    private static ServicePriceSchedule Pending(ServiceContract service, string effectiveFrom) =>
        ServicePriceSchedule.Schedule(service, null, 120000m, Date(effectiveFrom), "Ajuste", null, Now);

    [Fact]
    public void AdvanceReviewMovesWholeYearsPastTheResolvedPeriod()
    {
        var onTheReview = NewService();
        onTheReview.AdvanceReview(Date("2027-01-10"));
        Assert.Equal(Date("2028-01-10"), onTheReview.NextReviewDate);

        var afterAPause = NewService();
        afterAPause.AdvanceReview(Date("2029-03-10"));
        Assert.Equal(Date("2030-01-10"), afterAPause.NextReviewDate);
    }

    [Fact]
    public void AdvanceReviewClampsTheLeapDay()
    {
        var service = NewService(review: "2028-02-29");

        service.AdvanceReview(Date("2028-02-29"));

        Assert.Equal(Date("2029-02-28"), service.NextReviewDate);
    }

    // La suma es una sola (AddMonths(12 × k)): sumando de a un año, el 29 de febrero se perdería para siempre.
    [Fact]
    public void AdvanceReviewKeepsTheLeapDayAcrossSeveralYears()
    {
        var service = NewService(review: "2028-02-29");

        service.AdvanceReview(Date("2031-03-10"));

        Assert.Equal(Date("2032-02-29"), service.NextReviewDate);
    }

    [Fact]
    public void TheReviewPeriodIsTheFirstUngeneratedAnchorOnOrAfterTheReviewDate()
    {
        Assert.Equal(Date("2027-01-10"), PriceReview.ReviewPeriodOf(NewService(review: "2027-01-10")));
        Assert.Equal(Date("2027-02-10"), PriceReview.ReviewPeriodOf(NewService(review: "2027-01-11")));
        // La revisión quedó atrás (generó «sin avanzar» o se pausó): el primer período sin generar.
        Assert.Equal(Date("2027-03-10"), PriceReview.ReviewPeriodOf(NewService(review: "2027-01-10", lastGenerated: "2026-12-10").Resumed("2027-03-01")));
    }

    [Fact]
    public void TheStateFollowsTheTableInOrder()
    {
        var upcoming = PriceReview.Of(NewService(), null, Date("2026-12-25"));
        Assert.Equal(ReviewState.Upcoming, upcoming.State);

        var due = PriceReview.Of(NewService(), null, Date("2026-12-26"));
        Assert.Equal(ReviewState.Due, due.State);

        var covered = NewService();
        var schedule = Pending(covered, "2027-01-10");
        var scheduled = PriceReview.Of(covered, schedule, Date("2026-12-26"));
        Assert.Equal(ReviewState.Scheduled, scheduled.State);
        Assert.Equal(Date("2027-01-10"), scheduled.ScheduleAppliesTo);

        // Un programado que aplica antes de la revisión no la cubre.
        var early = NewService(review: "2027-03-10");
        var earlySchedule = Pending(early, "2027-01-10");
        Assert.Equal(ReviewState.Upcoming, PriceReview.Of(early, earlySchedule, Date("2026-12-25")).State);
        Assert.Equal(Date("2027-01-10"), PriceReview.Of(early, earlySchedule, Date("2026-12-25")).ScheduleAppliesTo);

        // Held gana aunque haya un programado que cubra.
        var held = NewService();
        var heldSchedule = Pending(held, "2027-01-10");
        held.Hold(Date("2027-01-05"));
        Assert.Equal(ReviewState.Held, PriceReview.Of(held, heldSchedule, Date("2027-01-05")).State);

        var paused = NewService();
        paused.Pause(Date("2026-03-10"), Now);
        var none = PriceReview.Of(paused, null, Date("2027-01-05"));
        Assert.Equal(ReviewState.None, none.State);
        Assert.Null(none.ReviewPeriodDate);
        Assert.Null(none.GenerationDate);
        Assert.Null(none.NoticeFrom);
        Assert.Null(none.IpcYear);
        Assert.Null(none.ScheduleAppliesTo);
    }

    [Fact]
    public void TheNoticeStartsTenDaysBeforeGeneration()
    {
        var review = PriceReview.Of(NewService(), null, Date("2026-12-01"));

        Assert.Equal(Date("2027-01-10"), review.ReviewPeriodDate);
        Assert.Equal(Date("2027-01-05"), review.GenerationDate);
        Assert.Equal(Date("2026-12-26"), review.NoticeFrom);
    }

    [Fact]
    public void TheIpcYearIsTheYearBeforeTheReview()
    {
        Assert.Equal(2026, PriceReview.Of(NewService(AdjustmentMode.Ipc), null, Date("2026-12-01")).IpcYear);
        Assert.Null(PriceReview.Of(NewService(AdjustmentMode.Manual), null, Date("2026-12-01")).IpcYear);
    }

    [Fact]
    public void IpcPriceRoundsWithTheTenantRule()
    {
        var cop = PriceReview.IpcPrice(100000m, 5.20m, 2m, new PriceRounding(0, PriceRoundingMode.Nearest));
        Assert.Equal("107200.00", cop!.Value.ToString(CultureInfo.InvariantCulture));

        Assert.Equal(105.19m, PriceReview.IpcPrice(99.99m, 5.2m, 0m, new PriceRounding(2, PriceRoundingMode.Nearest)));
        Assert.Null(PriceReview.IpcPrice(100m, -100m, 0m, new PriceRounding(2, PriceRoundingMode.Nearest)));
    }

    [Fact]
    public void TheIpcReasonCarriesYearRateAndPoints()
    {
        Assert.Equal("IPC 2026 (5,20 %) + 2,00 puntos", PriceReview.IpcReason(2026, 5.2m, 2m));
        Assert.Equal("IPC 2026 (5,20 %)", PriceReview.IpcReason(2026, 5.2m, 0m));
    }
}

internal static class PriceReviewTestExtensions
{
    /// <summary>Pausa y reanuda: lo que cayó en la pausa no se genera.</summary>
    public static ServiceContract Resumed(this ServiceContract service, string today)
    {
        var now = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
        service.Pause(DateOnly.FromDateTime(now.UtcDateTime), now);
        service.Resume(DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture), now);
        return service;
    }
}
