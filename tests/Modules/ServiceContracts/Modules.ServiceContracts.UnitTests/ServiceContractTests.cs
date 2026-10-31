using System.Globalization;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

public sealed class ServiceContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static ServiceContract NewService(BillingCycle cycle = BillingCycle.Monthly, string first = "2026-01-10") =>
        ServiceContract.Create(
            ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", 100m, "COP",
            cycle, DateOnly.Parse(first, CultureInfo.InvariantCulture), Now);

    private static void AssertCode(string code, Action act) =>
        Assert.Equal(code, Assert.Throws<ServiceContractsDomainException>(act).Code);

    [Fact]
    public void StatusChangedAtStartsAtCreationAndMovesOnlyWithStatusTransitions()
    {
        var service = NewService();
        Assert.Equal(Now, service.StatusChangedAt);

        service.Update("Hosting 2", 120m, "COP", "Ajuste anual", null, Now.AddHours(1));
        Assert.Equal(Now, service.StatusChangedAt);

        service.Pause(Now.AddDays(1));
        Assert.Equal(Now.AddDays(1), service.StatusChangedAt);

        service.Resume(new DateOnly(2026, 3, 12), Now.AddDays(2));
        Assert.Equal(Now.AddDays(2), service.StatusChangedAt);

        service.Cancel(Now.AddDays(3));
        Assert.Equal(Now.AddDays(3), service.StatusChangedAt);
    }

    // El alta responde con el precio del request y las lecturas lo traen de numeric(18,2): sin una escala fija, 150000 y 150000.00.
    [Fact]
    public void ThePriceAlwaysCarriesTwoDecimalsOnCreateAndUpdate()
    {
        var service = ServiceContract.Create(
            ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", 150000m, "COP",
            BillingCycle.Monthly, new DateOnly(2026, 1, 10), Now);
        Assert.Equal("150000.00", service.Price.ToString(CultureInfo.InvariantCulture));

        service.Update("Hosting", 20.5m, "USD", "Pasa a dólares", null, Now);
        Assert.Equal("20.50", service.Price.ToString(CultureInfo.InvariantCulture));

        var charge = Charge.Generate(service, new DateOnly(2026, 2, 10), Now);
        Assert.Equal("20.50", charge.Amount.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void CreateRequiresANameAndAPositivePrice()
    {
        foreach (var name in new[] { "", "   ", new string('a', 151) })
        {
            AssertCode(ServiceContractsErrorCodes.ServiceInvalid, () => ServiceContract.Create(
                ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), name, 10m, "COP",
                BillingCycle.Monthly, new DateOnly(2026, 1, 1), Now));
        }

        foreach (var price in new[] { 0m, -1m })
        {
            AssertCode(ServiceContractsErrorCodes.ServiceInvalid, () => ServiceContract.Create(
                ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), "Hosting", price, "COP",
                BillingCycle.Monthly, new DateOnly(2026, 1, 1), Now));
        }
    }

    [Fact]
    public void CreateTrimsTheNameAndStartsActive()
    {
        var service = ServiceContract.Create(
            ServiceContractId.New(), Guid.NewGuid(), Guid.NewGuid(), "  Hosting ", 10m, "COP",
            BillingCycle.Annual, new DateOnly(2026, 1, 1), Now);

        Assert.Equal("Hosting", service.Name);
        Assert.Equal(ServiceContractStatus.Active, service.Status);
        Assert.Null(service.LastGeneratedDueDate);
        Assert.Null(service.ReactivatedOn);
    }

    [Fact]
    public void PauseOnlyFromActiveAndResumeOnlyFromPaused()
    {
        var service = NewService();
        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => service.Resume(new DateOnly(2026, 3, 10), Now));

        service.Pause(Now);
        Assert.Equal(ServiceContractStatus.Paused, service.Status);
        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => service.Pause(Now));

        service.Resume(new DateOnly(2026, 3, 10), Now);
        Assert.Equal(ServiceContractStatus.Active, service.Status);
    }

    [Fact]
    public void CancelFromActiveOrPausedAndNeverBack()
    {
        var active = NewService();
        active.Cancel(Now);
        Assert.Equal(ServiceContractStatus.Cancelled, active.Status);

        var paused = NewService();
        paused.Pause(Now);
        paused.Cancel(Now);
        Assert.Equal(ServiceContractStatus.Cancelled, paused.Status);

        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => active.Resume(new DateOnly(2026, 3, 10), Now));
        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => active.Pause(Now));
        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => active.Cancel(Now));
    }

    [Fact]
    public void UpdateIsRefusedOnACancelledService()
    {
        var service = NewService();
        service.Cancel(Now);

        AssertCode(ServiceContractsErrorCodes.ServiceCancelled, () => service.Update("Other", 5m, "COP", "x", null, Now));
    }

    [Fact]
    public void UpdateChangesNamePriceAndCurrencyAndBumpsVersion()
    {
        var service = NewService();
        var version = service.Version;

        service.Update(" Dominio ", 250m, "USD", "Renovación", null, Now.AddDays(1));

        Assert.Equal("Dominio", service.Name);
        Assert.Equal(250m, service.Price);
        Assert.Equal("USD", service.Currency);
        Assert.Equal(version + 1, service.Version);
        Assert.Equal(Now.AddDays(1), service.UpdatedAt);
    }

    [Fact]
    public void UpdateValidatesLikeCreate() =>
        AssertCode(ServiceContractsErrorCodes.ServiceInvalid, () => NewService().Update("", 5m, "COP", "x", null, Now));

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

    // El historial de precios: cada cambio de precio o moneda deja una fila con el antes, el después, el motivo y quién lo hizo.
    [Fact]
    public void UpdateRecordsAPriceChangeWithItsReasonAndAuthor()
    {
        var service = NewService();
        var author = Guid.NewGuid();

        var change = service.Update("Hosting", 120m, "COP", "  Incremento del IPC 2026  ", author, Now.AddDays(1));

        Assert.NotNull(change);
        Assert.Equal(service.Id, change.ServiceId);
        Assert.Equal(service.TenantId, change.TenantId);
        Assert.Equal(100m, change.PreviousPrice);
        Assert.Equal("COP", change.PreviousCurrency);
        Assert.Equal(120m, change.NewPrice);
        Assert.Equal("COP", change.NewCurrency);
        Assert.Equal("Incremento del IPC 2026", change.Reason);
        Assert.Equal(author, change.ChangedByMemberId);
        Assert.Equal(Now.AddDays(1), change.OccurredAt);
        Assert.NotEqual(Guid.Empty, change.Id);
    }

    [Fact]
    public void ChangingOnlyTheCurrencyIsAPriceChange()
    {
        var change = NewService().Update("Hosting", 100m, "USD", "El cliente paga en dólares", null, Now);

        Assert.NotNull(change);
        Assert.Equal("COP", change.PreviousCurrency);
        Assert.Equal("USD", change.NewCurrency);
        Assert.Null(change.ChangedByMemberId);
    }

    // 100 y 100.00 son el mismo precio: renombrar el servicio no escribe historial ni exige motivo.
    [Fact]
    public void UpdateWithTheSamePriceRecordsNothingAndNeedsNoReason()
    {
        var service = NewService();

        Assert.Null(service.Update("Hosting Pro", 100.00m, "COP", null, null, Now));
        Assert.Null(service.Update("Hosting Pro", 100m, "COP", "   ", null, Now));
        Assert.Equal("Hosting Pro", service.Name);
    }

    [Fact]
    public void UpdateRequiresAReasonWhenThePriceOrTheCurrencyChanges()
    {
        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => NewService().Update("Hosting", 120m, "COP", null, null, Now));
        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => NewService().Update("Hosting", 120m, "COP", "   ", null, Now));
        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => NewService().Update("Hosting", 100m, "USD", "", null, Now));
    }

    [Fact]
    public void UpdateRejectsAReasonLongerThanTheLimit()
    {
        var service = NewService();
        var tooLong = new string('a', ServicePriceChange.ReasonMaxLength + 1);

        AssertCode(ServiceContractsErrorCodes.ServiceInvalid, () => service.Update("Hosting", 120m, "COP", tooLong, null, Now));
        Assert.Equal(100m, service.Price);
    }

    // Un cambio rechazado no deja el agregado a medias: ni el nombre ni la versión se mueven.
    [Fact]
    public void ARefusedPriceChangeLeavesTheServiceUntouched()
    {
        var service = NewService();

        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => service.Update("Otro", 120m, "COP", null, null, Now.AddDays(1)));

        Assert.Equal("Hosting", service.Name);
        Assert.Equal(100m, service.Price);
        Assert.Equal(1, service.Version);
        Assert.Equal(Now, service.UpdatedAt);
    }
}
