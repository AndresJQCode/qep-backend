namespace Modules.ServiceContracts.Domain;

/// <summary>Quién fijó el precio programado: una persona a mano o la confirmación del IPC del año.</summary>
public enum PriceScheduleSource
{
    Manual = 1,
    Ipc = 2,
}

public enum PriceScheduleStatus
{
    Pending = 1,
    Applied = 2,
    Cancelled = 3,
}

/// <summary>
/// Un precio nuevo que aplica desde el primer período generado con <c>period_date &gt;= effective_from</c> (spec reajuste §5):
/// queda atado al período y no al día en que alguien lo escribió. Hay a lo sumo uno <c>Pending</c> por contrato. Lo aplica
/// <see cref="ChargeGenerator"/>, en la misma transacción que el cobro.
/// </summary>
public sealed class ServicePriceSchedule
{
    // EF Core materializa por acá; Schedule y ConfirmIpc son los puntos de entrada de código.
    private ServicePriceSchedule()
    {
        Currency = string.Empty;
        Reason = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public ServiceContractId ServiceId { get; private set; }

    public DateOnly EffectiveFrom { get; private set; }

    public decimal NewPrice { get; private set; }

    /// <summary>La del contrato al programar: cambiar la moneda con un programado pendiente se rechaza.</summary>
    public string Currency { get; private set; }

    public PriceScheduleSource Source { get; private set; }

    public int? IpcYear { get; private set; }

    /// <summary>Copiada al confirmar: editar la tasa del año después no toca lo ya programado.</summary>
    public decimal? IpcRate { get; private set; }

    public decimal? ExtraPoints { get; private set; }

    public string Reason { get; private set; }

    /// <summary>Nula si quien programó no tenía membresía activa, como <c>ServicePriceChange.ChangedByMemberId</c>.</summary>
    public Guid? CreatedByMemberId { get; private set; }

    public PriceScheduleStatus Status { get; private set; }

    public DateOnly? AppliedPeriodDate { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? AppliedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <param name="pending">El programado pendiente del contrato, si ya tiene uno.</param>
    /// <param name="effectiveFrom">Nulo es el período de revisión.</param>
    public static ServicePriceSchedule Schedule(
        ServiceContract service,
        ServicePriceSchedule? pending,
        decimal newPrice,
        DateOnly? effectiveFrom,
        string? reason,
        Guid? memberId,
        DateTimeOffset now)
    {
        service.EnsureCanChangePrice();
        if (pending is not null)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.PriceScheduleAlreadyPending, "The service already has a pending price change.");
        }

        var from = effectiveFrom ?? PriceReview.ReviewPeriodOf(service);
        if (service.LastGeneratedPeriodDate is { } last && from <= last)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.PriceSchedulePeriodGenerated, "That period was already generated.");
        }

        // En Ipc la revisión retendría antes de llegar a un cambio posterior, que quedaría colgado (spec reajuste §5).
        if (service.AdjustmentMode == AdjustmentMode.Ipc && PriceReview.AppliesTo(service, from) > PriceReview.ReviewPeriodOf(service))
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.PriceScheduleAfterReview,
                "An IPC service cannot have a price change after its review period.");
        }

        var price = ServiceContract.ToMoney(newPrice);
        if (price == service.Price)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.PriceScheduleSamePrice, "The new price is the current price.");
        }

        var schedule = Create(service, price, from, ServiceContract.ValidateReason(reason), memberId, now);
        service.Touch(now);
        return schedule;
    }

    /// <summary>
    /// La confirmación del IPC de un contrato (spec reajuste §6.7): aplica en el período de revisión y copia la tasa y los
    /// puntos. Quien llama ya comprobó que el contrato esté por revisar y calculó el precio.
    /// </summary>
    public static ServicePriceSchedule ConfirmIpc(
        ServiceContract service,
        int ipcYear,
        decimal ipcRate,
        decimal newPrice,
        string reason,
        Guid? memberId,
        DateTimeOffset now)
    {
        var schedule = Create(
            service,
            ServiceContract.ToMoney(newPrice),
            PriceReview.ReviewPeriodOf(service),
            ServiceContract.ValidateReason(reason),
            memberId,
            now);
        schedule.Source = PriceScheduleSource.Ipc;
        schedule.IpcYear = ipcYear;
        schedule.IpcRate = ipcRate;
        schedule.ExtraPoints = service.IpcExtraPoints;
        service.Touch(now);
        return schedule;
    }

    public void Cancel(ServiceContract service, DateTimeOffset now)
    {
        RequirePending();
        Status = PriceScheduleStatus.Cancelled;
        CancelledAt = now;
        service.Touch(now);
    }

    internal void Apply(DateOnly period, DateTimeOffset now)
    {
        RequirePending();
        Status = PriceScheduleStatus.Applied;
        AppliedPeriodDate = period;
        AppliedAt = now;
    }

    private static ServicePriceSchedule Create(
        ServiceContract service,
        decimal price,
        DateOnly effectiveFrom,
        string reason,
        Guid? memberId,
        DateTimeOffset now)
    {
        if (price <= 0)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ServiceInvalid, "The price must be greater than zero.");
        }

        return new ServicePriceSchedule
        {
            Id = Guid.CreateVersion7(),
            TenantId = service.TenantId,
            ServiceId = service.Id,
            EffectiveFrom = effectiveFrom,
            NewPrice = price,
            Currency = service.Currency,
            Source = PriceScheduleSource.Manual,
            Reason = reason,
            CreatedByMemberId = memberId,
            Status = PriceScheduleStatus.Pending,
            CreatedAt = now,
        };
    }

    private void RequirePending()
    {
        if (Status != PriceScheduleStatus.Pending)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.PriceScheduleNotFound, "The service has no pending price change.");
        }
    }
}
