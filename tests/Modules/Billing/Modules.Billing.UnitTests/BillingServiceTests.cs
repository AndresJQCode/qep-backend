using System.Globalization;
using Modules.Billing.Domain;

namespace Modules.Billing.UnitTests;

public sealed class BillingServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static BillingService NewService(BillingCycle cycle = BillingCycle.Monthly, string first = "2026-01-10") =>
        BillingService.Create(
            BillingServiceId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", 100m, "COP",
            cycle, DateOnly.Parse(first, CultureInfo.InvariantCulture), Now);

    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<BillingDomainException>(act).Code);

    [Fact]
    public void CreateRequiresANameAndAPositivePrice()
    {
        foreach (var name in new[] { "", "   ", new string('a', 151) })
        {
            AssertCode(BillingErrorCodes.ServiceInvalid, () => BillingService.Create(
                BillingServiceId.New(), Guid.NewGuid(), Guid.NewGuid(), name, 10m, "COP",
                BillingCycle.Monthly, new DateOnly(2026, 1, 1), Now));
        }

        foreach (var price in new[] { 0m, -1m })
        {
            AssertCode(BillingErrorCodes.ServiceInvalid, () => BillingService.Create(
                BillingServiceId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", price, "COP",
                BillingCycle.Monthly, new DateOnly(2026, 1, 1), Now));
        }
    }

    [Fact]
    public void CreateTrimsTheNameAndStartsActive()
    {
        var service = BillingService.Create(
            BillingServiceId.New(), Guid.NewGuid(), Guid.NewGuid(), "  Hosting ", 10m, "COP",
            BillingCycle.Annual, new DateOnly(2026, 1, 1), Now);

        Assert.Equal("Hosting", service.Name);
        Assert.Equal(BillingServiceStatus.Active, service.Status);
        Assert.Null(service.LastGeneratedDueDate);
        Assert.Null(service.ReactivatedOn);
    }

    [Fact]
    public void PauseOnlyFromActiveAndResumeOnlyFromPaused()
    {
        var service = NewService();
        AssertCode(BillingErrorCodes.ServiceInvalidTransition, () => service.Resume(new DateOnly(2026, 3, 10), Now));

        service.Pause(Now);
        Assert.Equal(BillingServiceStatus.Paused, service.Status);
        AssertCode(BillingErrorCodes.ServiceInvalidTransition, () => service.Pause(Now));

        service.Resume(new DateOnly(2026, 3, 10), Now);
        Assert.Equal(BillingServiceStatus.Active, service.Status);
    }

    [Fact]
    public void CancelFromActiveOrPausedAndNeverBack()
    {
        var active = NewService();
        active.Cancel(Now);
        Assert.Equal(BillingServiceStatus.Cancelled, active.Status);

        var paused = NewService();
        paused.Pause(Now);
        paused.Cancel(Now);
        Assert.Equal(BillingServiceStatus.Cancelled, paused.Status);

        AssertCode(BillingErrorCodes.ServiceInvalidTransition, () => active.Resume(new DateOnly(2026, 3, 10), Now));
        AssertCode(BillingErrorCodes.ServiceInvalidTransition, () => active.Pause(Now));
        AssertCode(BillingErrorCodes.ServiceInvalidTransition, () => active.Cancel(Now));
    }

    [Fact]
    public void UpdateIsRefusedOnACancelledService()
    {
        var service = NewService();
        service.Cancel(Now);

        AssertCode(BillingErrorCodes.ServiceCancelled, () => service.Update("Other", 5m, "COP", Now));
    }

    [Fact]
    public void UpdateChangesNamePriceAndCurrencyAndBumpsVersion()
    {
        var service = NewService();
        var version = service.Version;

        service.Update(" Dominio ", 250m, "USD", Now.AddDays(1));

        Assert.Equal("Dominio", service.Name);
        Assert.Equal(250m, service.Price);
        Assert.Equal("USD", service.Currency);
        Assert.Equal(version + 1, service.Version);
        Assert.Equal(Now.AddDays(1), service.UpdatedAt);
    }

    [Fact]
    public void UpdateValidatesLikeCreate() =>
        AssertCode(BillingErrorCodes.ServiceInvalid, () => NewService().Update("", 5m, "COP", Now));

    [Fact]
    public void ResumeRecordsTheReactivationDate()
    {
        var service = NewService();
        service.Pause(Now);

        service.Resume(new DateOnly(2026, 5, 3), Now);

        Assert.Equal(new DateOnly(2026, 5, 3), service.ReactivatedOn);
    }

    [Fact]
    public void NextDueDateToGenerateStartsAtTheFirstDueDate() =>
        Assert.Equal(new DateOnly(2026, 1, 10), NewService().NextDueDateToGenerate());

    [Fact]
    public void NextDueDateToGenerateIsTheAnchorAfterTheLastOne()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 2, 10));

        Assert.Equal(new DateOnly(2026, 3, 10), service.NextDueDateToGenerate());
    }

    [Fact]
    public void NextDueDateToGenerateAfterAResumeSkipsThePausedPeriods()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 2, 10));
        service.Pause(Now);
        service.Resume(new DateOnly(2026, 5, 3), Now);

        Assert.Equal(new DateOnly(2026, 5, 10), service.NextDueDateToGenerate());

        var onTheAnchor = NewService();
        onTheAnchor.MarkGenerated(new DateOnly(2026, 2, 10));
        onTheAnchor.Pause(Now);
        onTheAnchor.Resume(new DateOnly(2026, 5, 10), Now);

        Assert.Equal(new DateOnly(2026, 5, 10), onTheAnchor.NextDueDateToGenerate());
    }

    [Fact]
    public void AResumeBeforeTheNextAnchorDoesNotMoveIt()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 2, 10));
        service.Pause(Now);
        service.Resume(new DateOnly(2026, 2, 20), Now);

        Assert.Equal(new DateOnly(2026, 3, 10), service.NextDueDateToGenerate());
    }

    [Fact]
    public void NextDueDateToGenerateIsNullUnlessActive()
    {
        var paused = NewService();
        paused.Pause(Now);
        Assert.Null(paused.NextDueDateToGenerate());

        var cancelled = NewService();
        cancelled.Cancel(Now);
        Assert.Null(cancelled.NextDueDateToGenerate());
    }
}
