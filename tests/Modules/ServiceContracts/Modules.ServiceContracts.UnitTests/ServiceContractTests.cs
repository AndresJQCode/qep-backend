using System.Globalization;
using Modules.ServiceContracts.Domain;

namespace Modules.ServiceContracts.UnitTests;

public sealed class ServiceContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 3, 10);

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

        service.Update("Hosting 2", null, 120m, "COP", "Ajuste anual", null, Now.AddHours(1));
        Assert.Equal(Now, service.StatusChangedAt);

        service.Pause(Today.AddDays(1), Now.AddDays(1));
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

        service.Update("Hosting", null, 20.5m, "USD", "Pasa a dólares", null, Now);
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
        Assert.Null(service.LastGeneratedPeriodDate);
        Assert.Null(service.ReactivatedOn);
    }

    [Fact]
    public void PauseOnlyFromActiveAndResumeOnlyFromPaused()
    {
        var service = NewService();
        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => service.Resume(new DateOnly(2026, 3, 10), Now));

        service.Pause(Today, Now);
        Assert.Equal(ServiceContractStatus.Paused, service.Status);
        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => service.Pause(Today, Now));

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
        paused.Pause(Today, Now);
        paused.Cancel(Now);
        Assert.Equal(ServiceContractStatus.Cancelled, paused.Status);

        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => active.Resume(new DateOnly(2026, 3, 10), Now));
        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => active.Pause(Today, Now));
        AssertCode(ServiceContractsErrorCodes.ServiceInvalidTransition, () => active.Cancel(Now));
    }

    [Fact]
    public void UpdateIsRefusedOnACancelledService()
    {
        var service = NewService();
        service.Cancel(Now);

        AssertCode(ServiceContractsErrorCodes.ServiceCancelled, () => service.Update("Other", null, 5m, "COP", "x", null, Now));
    }

    [Fact]
    public void UpdateChangesNamePriceAndCurrencyAndBumpsVersion()
    {
        var service = NewService();
        var version = service.Version;

        service.Update(" Dominio ", null, 250m, "USD", "Renovación", null, Now.AddDays(1));

        Assert.Equal("Dominio", service.Name);
        Assert.Equal(250m, service.Price);
        Assert.Equal("USD", service.Currency);
        Assert.Equal(version + 1, service.Version);
        Assert.Equal(Now.AddDays(1), service.UpdatedAt);
    }

    [Fact]
    public void UpdateValidatesLikeCreate() =>
        AssertCode(ServiceContractsErrorCodes.ServiceInvalid, () => NewService().Update("", null, 5m, "COP", "x", null, Now));

    [Fact]
    public void ResumeRecordsTheReactivationDate()
    {
        var service = NewService();
        service.Pause(Today, Now);

        service.Resume(new DateOnly(2026, 5, 3), Now);

        Assert.Equal(new DateOnly(2026, 5, 3), service.ReactivatedOn);
    }

    [Fact]
    public void NextPeriodToGenerateStartsAtTheFirstDueDate() =>
        Assert.Equal(new DateOnly(2026, 1, 10), NewService().NextPeriodToGenerate());

    [Fact]
    public void NextPeriodToGenerateIsTheAnchorAfterTheLastOne()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 2, 10));

        Assert.Equal(new DateOnly(2026, 3, 10), service.NextPeriodToGenerate());
    }

    [Fact]
    public void NextPeriodToGenerateAfterAResumeSkipsThePausedPeriods()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 2, 10));
        service.Pause(Today, Now);
        service.Resume(new DateOnly(2026, 5, 3), Now);

        Assert.Equal(new DateOnly(2026, 5, 10), service.NextPeriodToGenerate());

        var onTheAnchor = NewService();
        onTheAnchor.MarkGenerated(new DateOnly(2026, 2, 10));
        onTheAnchor.Pause(Today, Now);
        onTheAnchor.Resume(new DateOnly(2026, 5, 10), Now);

        Assert.Equal(new DateOnly(2026, 5, 10), onTheAnchor.NextPeriodToGenerate());
    }

    [Fact]
    public void AResumeBeforeTheNextAnchorDoesNotMoveIt()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 2, 10));
        service.Pause(Today, Now);
        service.Resume(new DateOnly(2026, 2, 20), Now);

        Assert.Equal(new DateOnly(2026, 3, 10), service.NextPeriodToGenerate());
    }

    [Fact]
    public void NextPeriodToGenerateIsNullUnlessActive()
    {
        var paused = NewService();
        paused.Pause(Today, Now);
        Assert.Null(paused.NextPeriodToGenerate());

        var cancelled = NewService();
        cancelled.Cancel(Now);
        Assert.Null(cancelled.NextPeriodToGenerate());
    }

    [Fact]
    public void CreateStartsManualWithTheReviewAYearAfterTheAnchor()
    {
        var service = NewService();

        Assert.Equal(AdjustmentMode.Manual, service.AdjustmentMode);
        Assert.Equal(0m, service.IpcExtraPoints);
        Assert.Equal(new DateOnly(2027, 1, 10), service.NextReviewDate);
        Assert.Null(service.HeldSince);
    }

    [Fact]
    public void InitializeWithoutADateUsesTheAnniversaryAfterTheLastGenerated()
    {
        var future = NewService(first: "2026-06-01");
        future.MarkGenerated(new DateOnly(2026, 6, 1));
        future.InitializePriceReview(AdjustmentMode.Ipc, 2m, null);
        Assert.Equal(new DateOnly(2027, 6, 1), future.NextReviewDate);
        Assert.Equal(AdjustmentMode.Ipc, future.AdjustmentMode);
        Assert.Equal(2m, future.IpcExtraPoints);

        // Anclado hace 26 meses y generado hasta la ventana de hoy: el aniversario siguiente a ese último período.
        var migrated = NewService(first: "2024-01-10");
        migrated.MarkGenerated(new DateOnly(2026, 3, 10));
        var version = migrated.Version;
        migrated.InitializePriceReview(AdjustmentMode.Manual, 0m, null);
        Assert.Equal(new DateOnly(2027, 1, 10), migrated.NextReviewDate);
        Assert.Equal(version, migrated.Version);

        // El último generado cae justo en un aniversario: la revisión es el siguiente.
        var onTheAnniversary = NewService(first: "2024-03-10");
        onTheAnniversary.MarkGenerated(new DateOnly(2026, 3, 10));
        Assert.Equal(new DateOnly(2027, 3, 10), onTheAnniversary.DefaultNextReviewDate());
    }

    [Fact]
    public void AnExplicitReviewDateMustFollowTheLastGenerated()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 3, 10));

        AssertCode(
            ServiceContractsErrorCodes.ServiceReviewDateInvalid,
            () => service.InitializePriceReview(AdjustmentMode.Ipc, 0m, new DateOnly(2026, 3, 10)));
        service.InitializePriceReview(AdjustmentMode.Ipc, 0m, new DateOnly(2026, 3, 11));
        Assert.Equal(new DateOnly(2026, 3, 11), service.NextReviewDate);
    }

    [Fact]
    public void ManualStoresZeroPoints()
    {
        var service = NewService();
        service.InitializePriceReview(AdjustmentMode.Manual, 3m, null);
        Assert.Equal(0m, service.IpcExtraPoints);

        service.UpdatePriceReview(AdjustmentMode.Ipc, 3m, new DateOnly(2027, 1, 10), Now);
        Assert.Equal(3m, service.IpcExtraPoints);

        service.UpdatePriceReview(AdjustmentMode.Manual, 3m, new DateOnly(2027, 1, 10), Now);
        Assert.Equal(0m, service.IpcExtraPoints);
    }

    [Fact]
    public void ThePointsStayBetweenZeroAndFifty()
    {
        AssertCode(
            ServiceContractsErrorCodes.ServiceInvalid,
            () => NewService().InitializePriceReview(AdjustmentMode.Ipc, ServiceContract.MaxIpcExtraPoints + 0.01m, null));
        AssertCode(
            ServiceContractsErrorCodes.ServiceInvalid,
            () => NewService().InitializePriceReview(AdjustmentMode.Ipc, -0.01m, null));
    }

    [Fact]
    public void UpdatingThePriceReviewIsAllowedPausedAndRefusedCancelled()
    {
        var paused = NewService();
        paused.MarkGenerated(new DateOnly(2026, 3, 10));
        paused.Pause(Today, Now);
        var version = paused.Version;

        AssertCode(
            ServiceContractsErrorCodes.ServiceReviewDateInvalid,
            () => paused.UpdatePriceReview(AdjustmentMode.Ipc, 0m, new DateOnly(2026, 3, 10), Now));
        paused.UpdatePriceReview(AdjustmentMode.Ipc, 1m, new DateOnly(2026, 4, 1), Now.AddDays(1));

        Assert.Equal(new DateOnly(2026, 4, 1), paused.NextReviewDate);
        Assert.Equal(version + 1, paused.Version);
        Assert.Equal(Now.AddDays(1), paused.UpdatedAt);

        paused.Cancel(Now);
        AssertCode(
            ServiceContractsErrorCodes.ServiceCancelled,
            () => paused.UpdatePriceReview(AdjustmentMode.Ipc, 0m, new DateOnly(2027, 1, 10), Now));
    }

    [Fact]
    public void PauseAndCancelClearTheHold()
    {
        var paused = NewService();
        paused.Hold(new DateOnly(2026, 3, 10));
        Assert.Equal(new DateOnly(2026, 3, 10), paused.HeldSince);
        paused.Pause(Today, Now);
        Assert.Null(paused.HeldSince);

        var cancelled = NewService();
        cancelled.Hold(new DateOnly(2026, 3, 10));
        cancelled.Cancel(Now);
        Assert.Null(cancelled.HeldSince);
    }

    [Fact]
    public void UpcomingPeriodsStopAtTheReviewInIpc()
    {
        var manual = NewService();
        manual.MarkGenerated(new DateOnly(2026, 3, 10));
        Assert.Equal(12, manual.UpcomingPeriods().Count);
        Assert.Equal(new DateOnly(2026, 4, 10), manual.UpcomingPeriods()[0]);
        Assert.Equal(new DateOnly(2027, 3, 10), manual.UpcomingPeriods()[^1]);

        var ipc = NewService();
        ipc.MarkGenerated(new DateOnly(2026, 3, 10));
        ipc.InitializePriceReview(AdjustmentMode.Ipc, 0m, new DateOnly(2026, 6, 1));
        Assert.Equal(
            [new DateOnly(2026, 4, 10), new DateOnly(2026, 5, 10), new DateOnly(2026, 6, 10)],
            ipc.UpcomingPeriods());

        ipc.Pause(Today, Now);
        Assert.Empty(ipc.UpcomingPeriods());
    }

    [Fact]
    public void UpcomingPeriodsSkipThePauseButNotTheCarriedPeriods()
    {
        var service = PausedWhileHeld();
        service.UpdatePriceReview(AdjustmentMode.Manual, 0m, new DateOnly(2027, 1, 10), Now);
        service.Resume(new DateOnly(2027, 4, 20), Now);

        Assert.Equal(
            [
                new DateOnly(2027, 1, 10), new DateOnly(2027, 2, 10), new DateOnly(2027, 5, 10), new DateOnly(2027, 6, 10),
                new DateOnly(2027, 7, 10), new DateOnly(2027, 8, 10), new DateOnly(2027, 9, 10), new DateOnly(2027, 10, 10),
                new DateOnly(2027, 11, 10), new DateOnly(2027, 12, 10), new DateOnly(2028, 1, 10), new DateOnly(2028, 2, 10),
            ],
            service.UpcomingPeriods());
    }

    [Fact]
    public void AScheduleAppliesAlongTheCarriedPeriods()
    {
        var service = PausedWhileHeld();
        service.Resume(new DateOnly(2027, 4, 20), Now);

        Assert.Equal(new DateOnly(2027, 2, 10), PriceReview.AppliesTo(service, new DateOnly(2027, 2, 1)));
        // Marzo y abril cayeron en la pausa.
        Assert.Equal(new DateOnly(2027, 5, 10), PriceReview.AppliesTo(service, new DateOnly(2027, 3, 1)));
    }

    /// <summary>Un contrato <c>Ipc</c> retenido en enero de 2027 y pausado el 5 de febrero: arrastra enero y febrero.</summary>
    private static ServiceContract PausedWhileHeld()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 12, 10));
        service.InitializePriceReview(AdjustmentMode.Ipc, 0m, new DateOnly(2027, 1, 10));
        service.Hold(new DateOnly(2027, 1, 5));
        service.Pause(new DateOnly(2027, 2, 5), Now);
        return service;
    }

    // La pantalla de un contrato pausado y la invariante del reajuste necesitan el período aunque no se vaya a generar.
    [Fact]
    public void TheNextUngeneratedPeriodIgnoresTheStatus()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 3, 10));
        service.Pause(Today, Now);

        Assert.Null(service.NextPeriodToGenerate());
        Assert.Equal(new DateOnly(2026, 4, 10), service.NextUngeneratedPeriod());
    }

    [Fact]
    public void KeepPriceAdvancesFromTheReviewPeriod()
    {
        var service = NewService();
        service.MarkGenerated(new DateOnly(2026, 12, 10));
        service.InitializePriceReview(AdjustmentMode.Ipc, 0m, new DateOnly(2027, 1, 5));
        var version = service.Version;

        service.KeepPrice(Now.AddDays(1));

        Assert.Equal(new DateOnly(2028, 1, 5), service.NextReviewDate);
        Assert.Equal(100m, service.Price);
        Assert.Equal(version + 1, service.Version);
        Assert.Equal(Now.AddDays(1), service.UpdatedAt);

        service.Pause(Today, Now);
        AssertCode(ServiceContractsErrorCodes.ServiceNotActive, () => service.KeepPrice(Now));
        service.Cancel(Now);
        AssertCode(ServiceContractsErrorCodes.ServiceCancelled, () => service.KeepPrice(Now));
    }

    [Fact]
    public void AnEditRecordsItsPriceChangeAsAnEdit()
    {
        var change = NewService().Update("Hosting", null, 120m, "COP", "Ajuste", null, Now);

        Assert.Equal(PriceChangeOrigin.Edit, change!.Origin);
        Assert.Null(change.ScheduleId);
    }

    [Fact]
    public void AChargeCarriesItsPeriodApartFromItsDueDate()
    {
        var service = NewService();

        var onTheAnchor = Charge.Generate(service, new DateOnly(2026, 2, 10), Now);
        Assert.Equal(new DateOnly(2026, 2, 10), onTheAnchor.PeriodDate);
        Assert.Equal(new DateOnly(2026, 2, 10), onTheAnchor.DueDate);
        Assert.False(onTheAnchor.PriceUnreviewed);

        var released = Charge.Generate(service, new DateOnly(2026, 2, 10), new DateOnly(2026, 2, 19), true, Now);
        Assert.Equal(new DateOnly(2026, 2, 10), released.PeriodDate);
        Assert.Equal(new DateOnly(2026, 2, 19), released.DueDate);
        Assert.True(released.PriceUnreviewed);
    }

    // El historial de precios: cada cambio de precio o moneda deja una fila con el antes, el después, el motivo y quién lo hizo.
    [Fact]
    public void UpdateRecordsAPriceChangeWithItsReasonAndAuthor()
    {
        var service = NewService();
        var author = Guid.NewGuid();

        var change = service.Update("Hosting", null, 120m, "COP", "  Incremento del IPC 2026  ", author, Now.AddDays(1));

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
        var change = NewService().Update("Hosting", null, 100m, "USD", "El cliente paga en dólares", null, Now);

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

        Assert.Null(service.Update("Hosting Pro", null, 100.00m, "COP", null, null, Now));
        Assert.Null(service.Update("Hosting Pro", null, 100m, "COP", "   ", null, Now));
        Assert.Equal("Hosting Pro", service.Name);
    }

    [Fact]
    public void UpdateRequiresAReasonWhenThePriceOrTheCurrencyChanges()
    {
        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => NewService().Update("Hosting", null, 120m, "COP", null, null, Now));
        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => NewService().Update("Hosting", null, 120m, "COP", "   ", null, Now));
        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => NewService().Update("Hosting", null, 100m, "USD", "", null, Now));
    }

    [Fact]
    public void UpdateRejectsAReasonLongerThanTheLimit()
    {
        var service = NewService();
        var tooLong = new string('a', ServicePriceChange.ReasonMaxLength + 1);

        AssertCode(ServiceContractsErrorCodes.ServiceInvalid, () => service.Update("Hosting", null, 120m, "COP", tooLong, null, Now));
        Assert.Equal(100m, service.Price);
    }

    // Un cambio rechazado no deja el agregado a medias: ni el nombre ni la versión se mueven.
    [Fact]
    public void ARefusedPriceChangeLeavesTheServiceUntouched()
    {
        var service = NewService();

        AssertCode(ServiceContractsErrorCodes.PriceChangeReasonRequired, () => service.Update("Otro", null, 120m, "COP", null, null, Now.AddDays(1)));

        Assert.Equal("Hosting", service.Name);
        Assert.Equal(100m, service.Price);
        Assert.Equal(1, service.Version);
        Assert.Equal(Now, service.UpdatedAt);
    }
}
