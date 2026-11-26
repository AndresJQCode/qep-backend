using System.Globalization;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

/// <summary>El cambio de precio programado (spec reajuste §5): las reglas para crearlo, en su orden, y su cancelación.</summary>
public sealed class ServicePriceScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 3, 10);

    private static DateOnly Date(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static ServiceContract NewService(AdjustmentMode mode = AdjustmentMode.Manual)
    {
        var service = ServiceContract.Create(
            ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", 100m, "COP",
            BillingCycle.Monthly, Date("2026-01-10"), Now);
        service.MarkGenerated(Date("2026-03-10"));
        service.InitializePriceReview(mode, mode == AdjustmentMode.Ipc ? 1.5m : 0m, Date("2027-01-10"));
        return service;
    }

    private static ServicePriceSchedule Schedule(
        ServiceContract service,
        ServicePriceSchedule? pending = null,
        decimal newPrice = 120m,
        DateOnly? effectiveFrom = null,
        string? reason = "Más usuarios") =>
        ServicePriceSchedule.Schedule(service, pending, newPrice, effectiveFrom, reason, Guid.NewGuid(), Now);

    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<ServiceContractsDomainException>(act).Code);

    [Fact]
    public void ACancelledServiceCannotSchedule()
    {
        var service = NewService();
        service.Cancel(Now);

        AssertCode(ServiceContractsErrorCodes.ServiceCancelled, () => Schedule(service));
    }

    [Fact]
    public void APausedServiceIsNotActive()
    {
        var service = NewService();
        service.Pause(Today, Now);

        AssertCode(ServiceContractsErrorCodes.ServiceNotActive, () => Schedule(service));
    }

    [Fact]
    public void ASecondPendingIsAlreadyPending()
    {
        var service = NewService();
        var first = Schedule(service);

        // El pendiente se mira antes que la fecha, el precio y el motivo.
        AssertCode(
            ServiceContractsErrorCodes.PriceScheduleAlreadyPending,
            () => Schedule(service, first, newPrice: 100m, effectiveFrom: Date("2026-01-10"), reason: ""));
    }

    [Fact]
    public void AnEffectiveDateAlreadyGeneratedIsPeriodGenerated()
    {
        AssertCode(
            ServiceContractsErrorCodes.PriceSchedulePeriodGenerated,
            () => Schedule(NewService(), effectiveFrom: Date("2026-03-10"), newPrice: 100m));
    }

    [Fact]
    public void InIpcAChangeAfterTheReviewIsAfterReview()
    {
        AssertCode(
            ServiceContractsErrorCodes.PriceScheduleAfterReview,
            () => Schedule(NewService(AdjustmentMode.Ipc), effectiveFrom: Date("2027-01-11"), newPrice: 100m));

        // Un día después del anterior período todavía aplica a la revisión.
        var covering = Schedule(NewService(AdjustmentMode.Ipc), effectiveFrom: Date("2026-12-11"));
        Assert.Equal(Date("2026-12-11"), covering.EffectiveFrom);
    }

    [Fact]
    public void InManualAChangeAfterTheReviewIsAllowed()
    {
        var schedule = Schedule(NewService(), effectiveFrom: Date("2027-06-10"));

        Assert.Equal(Date("2027-06-10"), schedule.EffectiveFrom);
    }

    [Fact]
    public void TheCurrentPriceIsSamePrice()
    {
        AssertCode(ServiceContractsErrorCodes.PriceScheduleSamePrice, () => Schedule(NewService(), newPrice: 100m, reason: ""));
        AssertCode(ServiceContractsErrorCodes.PriceScheduleSamePrice, () => Schedule(NewService(), newPrice: 100.00m));
    }

    [Fact]
    public void AnEmptyReasonIsRequired()
    {
        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => Schedule(NewService(), reason: null));
        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => Schedule(NewService(), reason: "   "));
        AssertCode(
            ServiceContractsErrorCodes.ServiceInvalid,
            () => Schedule(NewService(), reason: new string('a', ServicePriceChange.ReasonMaxLength + 1)));
    }

    [Fact]
    public void ANullEffectiveDateMeansTheReviewPeriod()
    {
        var service = NewService();
        var member = Guid.NewGuid();

        var schedule = ServicePriceSchedule.Schedule(service, null, 120m, null, "  Más usuarios  ", member, Now);

        Assert.Equal(Date("2027-01-10"), schedule.EffectiveFrom);
        Assert.Equal(service.Id, schedule.ServiceId);
        Assert.Equal(service.TenantId, schedule.TenantId);
        Assert.Equal("120.00", schedule.NewPrice.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("COP", schedule.Currency);
        Assert.Equal(PriceScheduleSource.Manual, schedule.Source);
        Assert.Equal(PriceScheduleStatus.Pending, schedule.Status);
        Assert.Null(schedule.IpcYear);
        Assert.Null(schedule.IpcRate);
        Assert.Null(schedule.ExtraPoints);
        Assert.Equal("Más usuarios", schedule.Reason);
        Assert.Equal(member, schedule.CreatedByMemberId);
        Assert.Equal(Now, schedule.CreatedAt);
        Assert.Null(schedule.AppliedPeriodDate);
        // Programar no cambia el precio todavía.
        Assert.Equal(100m, service.Price);
    }

    [Fact]
    public void SchedulingBumpsTheServiceVersion()
    {
        var service = NewService();
        var version = service.Version;

        Schedule(service);

        Assert.Equal(version + 1, service.Version);
    }

    [Fact]
    public void ConfirmIpcCopiesTheRateAndThePointsOntoTheReviewPeriod()
    {
        var service = NewService(AdjustmentMode.Ipc);
        var version = service.Version;

        var schedule = ServicePriceSchedule.ConfirmIpc(service, 2026, 5.2m, 107m, "IPC 2026 (5,20 %) + 1,50 puntos", null, Now);

        Assert.Equal(PriceScheduleSource.Ipc, schedule.Source);
        Assert.Equal(PriceScheduleStatus.Pending, schedule.Status);
        Assert.Equal(Date("2027-01-10"), schedule.EffectiveFrom);
        Assert.Equal(2026, schedule.IpcYear);
        Assert.Equal(5.2m, schedule.IpcRate);
        Assert.Equal(1.5m, schedule.ExtraPoints);
        Assert.Equal(107m, schedule.NewPrice);
        Assert.Equal(version + 1, service.Version);
    }

    [Fact]
    public void CancelOnlyCancelsAPending()
    {
        var service = NewService();
        var schedule = Schedule(service);
        var version = service.Version;

        schedule.Cancel(service, Now.AddHours(1));

        Assert.Equal(PriceScheduleStatus.Cancelled, schedule.Status);
        Assert.Equal(Now.AddHours(1), schedule.CancelledAt);
        Assert.Equal(version + 1, service.Version);
        AssertCode(ServiceContractsErrorCodes.PriceScheduleNotFound, () => schedule.Cancel(service, Now));
        Assert.Equal(version + 1, service.Version);
    }
}
