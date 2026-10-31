namespace Modules.ServiceContracts.Domain;

/// <summary>
/// Un servicio recurrente que el tenant le cobra a un cliente. Guarda la ancla
/// (<see cref="FirstDueDate"/>) y hasta qué vencimiento ya generó cobros
/// (<see cref="LastGeneratedDueDate"/>); los cobros en sí son <see cref="Charge"/>.
/// </summary>
public sealed class ServiceContract
{
    public const int NameMaxLength = 150;

    public const int DescriptionMaxLength = 1000;

    // EF Core materializa por acá; Create es el único punto de entrada de código.
    private ServiceContract()
    {
        Name = string.Empty;
        Currency = string.Empty;
    }

    public ServiceContractId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>FK blanda a Customers: Guid y no un id tipado, como en el resto de módulos.</summary>
    public Guid CustomerId { get; private set; }

    public string Name { get; private set; }

    /// <summary>What the contract includes, as the tenant wrote it. Free text and internal: it never reaches a customer notice.</summary>
    public string? Description { get; private set; }

    public decimal Price { get; private set; }

    public string Currency { get; private set; }

    public BillingCycle Cycle { get; private set; }

    public DateOnly FirstDueDate { get; private set; }

    public ServiceContractStatus Status { get; private set; }

    /// <summary>Cuándo se reanudó por última vez; sirve para saltar los períodos pausados.</summary>
    public DateOnly? ReactivatedOn { get; private set; }

    public DateOnly? LastGeneratedDueDate { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Cuándo cambió <see cref="Status"/> por última vez; al crear vale lo mismo que <see cref="CreatedAt"/> (spec §2).</summary>
    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <param name="description">Optional, and last so the Excel import, which has no such column, can leave it out.</param>
    public static ServiceContract Create(
        ServiceContractId id,
        Guid tenantId,
        Guid customerId,
        string name,
        decimal price,
        string currency,
        BillingCycle cycle,
        DateOnly firstDueDate,
        DateTimeOffset now,
        string? description = null)
    {
        var (cleanName, cleanCurrency) = Validate(name, price, currency);
        var cleanDescription = CleanDescription(description);
        return new ServiceContract
        {
            Id = id,
            TenantId = tenantId,
            CustomerId = customerId,
            Name = cleanName,
            Description = cleanDescription,
            Price = ToMoney(price),
            Currency = cleanCurrency,
            Cycle = cycle,
            FirstDueDate = firstDueDate,
            Status = ServiceContractStatus.Active,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            StatusChangedAt = now,
        };
    }

    /// <summary>
    /// Cambia nombre, precio y moneda. Si el precio o la moneda cambian de verdad (100 y 100.00 son el mismo precio) el cambio
    /// exige <paramref name="priceChangeReason"/> y devuelve la fila del historial, que quien llama persiste en la misma
    /// transacción; si no, el motivo se ignora y devuelve null. Un cambio rechazado no toca nada: ni el nombre ni la versión.
    /// <paramref name="description"/> replaces the stored one (null clears it) and never counts as a price change.
    /// </summary>
    public ServicePriceChange? Update(
        string name,
        string? description,
        decimal price,
        string currency,
        string? priceChangeReason,
        Guid? changedByMemberId,
        DateTimeOffset now)
    {
        if (Status == ServiceContractStatus.Cancelled)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ServiceCancelled, "A cancelled service cannot be edited.");
        }

        var (cleanName, cleanCurrency) = Validate(name, price, currency);
        var cleanDescription = CleanDescription(description);
        var cleanPrice = ToMoney(price);
        var priceChanged = cleanPrice != Price || !string.Equals(cleanCurrency, Currency, StringComparison.Ordinal);
        var reason = priceChanged ? ValidateReason(priceChangeReason) : null;

        var previousPrice = Price;
        var previousCurrency = Currency;
        Name = cleanName;
        Description = cleanDescription;
        Price = cleanPrice;
        Currency = cleanCurrency;
        Touch(now);

        return reason is null
            ? null
            : ServicePriceChange.Record(this, previousPrice, previousCurrency, reason, changedByMemberId, now);
    }

    public void Pause(DateTimeOffset now)
    {
        RequireStatus(ServiceContractStatus.Active);
        Status = ServiceContractStatus.Paused;
        StatusChangedAt = now;
        Touch(now);
    }

    public void Resume(DateOnly today, DateTimeOffset now)
    {
        RequireStatus(ServiceContractStatus.Paused);
        Status = ServiceContractStatus.Active;
        ReactivatedOn = today;
        StatusChangedAt = now;
        Touch(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status == ServiceContractStatus.Cancelled)
        {
            throw InvalidTransition();
        }

        Status = ServiceContractStatus.Cancelled;
        StatusChangedAt = now;
        Touch(now);
    }

    /// <summary>
    /// El próximo vencimiento a generar (spec §3.2), o null si el servicio no está activo. Tras una
    /// reanudación se salta lo que cayó en la pausa: la primera ancla igual o posterior a la fecha
    /// de reactivación.
    /// </summary>
    public DateOnly? NextDueDateToGenerate()
    {
        if (Status != ServiceContractStatus.Active)
        {
            return null;
        }

        if (LastGeneratedDueDate is not { } last)
        {
            return FirstDueDate;
        }

        return ReactivatedOn is { } reactivated && reactivated > last
            ? BillingCycles.FirstDueDateOnOrAfter(FirstDueDate, Cycle, reactivated)
            : BillingCycles.NextDueDate(FirstDueDate, Cycle, last);
    }

    public void MarkGenerated(DateOnly dueDate) => LastGeneratedDueDate = dueDate;

    /// <summary>
    /// Lo llama la corrida diaria cuando generó cobros sobre un servicio ya guardado. Sin esto, una cancelación que cargó el servicio
    /// y sus <c>Pending</c> antes de que la corrida confirmara seguiría casando su <c>WHERE version</c> y dejaría el servicio
    /// <c>Cancelled</c> con un <c>Pending</c> que nunca vio; con la versión arriba pierde con 412 y se reintenta sobre lo nuevo.
    /// No vive en <see cref="MarkGenerated"/>: crear e importar también generan, sobre un servicio que nace en la misma transacción
    /// y cuya versión tiene que seguir siendo 1. <see cref="UpdatedAt"/> no se toca: nadie editó el servicio.
    /// </summary>
    public void BumpVersionAfterGeneration() => Version++;

    /// <summary>
    /// El precio con la escala de <c>numeric(18,2)</c> (siempre dos decimales): lo que se responde al crear es lo que la base
    /// devuelve después, y los cobros que copian el precio heredan la misma escala. El validador del borde ya rechaza más de dos
    /// decimales; acá nunca se pierde un dígito significativo.
    /// </summary>
    private static decimal ToMoney(decimal value) => decimal.Round(value, 2) + 0.00m;

    private static (string Name, string Currency) Validate(string name, decimal price, string currency)
    {
        var cleanName = name?.Trim() ?? string.Empty;
        if (cleanName.Length is 0 or > NameMaxLength)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ServiceInvalid, $"The name must have between 1 and {NameMaxLength} characters.");
        }

        if (price <= 0)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ServiceInvalid, "The price must be greater than zero.");
        }

        var cleanCurrency = currency?.Trim() ?? string.Empty;
        if (cleanCurrency.Length == 0)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ServiceInvalid, "The currency is required.");
        }

        return (cleanName, cleanCurrency);
    }

    /// <summary>Trimmed, with blank as null: the limit is measured on what gets stored, so padding never costs the user a 422.</summary>
    private static string? CleanDescription(string? description)
    {
        var clean = description?.Trim();
        if (string.IsNullOrEmpty(clean))
        {
            return null;
        }

        if (clean.Length > DescriptionMaxLength)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.ServiceDescriptionTooLong, $"The description cannot exceed {DescriptionMaxLength} characters.");
        }

        return clean;
    }

    private static string ValidateReason(string? reason)
    {
        var clean = reason?.Trim() ?? string.Empty;
        if (clean.Length == 0)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.PriceChangeReasonRequired, "A reason is required to change the price or the currency.");
        }

        if (clean.Length > ServicePriceChange.ReasonMaxLength)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.ServiceInvalid, $"The price change reason must have at most {ServicePriceChange.ReasonMaxLength} characters.");
        }

        return clean;
    }

    private void RequireStatus(ServiceContractStatus expected)
    {
        if (Status != expected)
        {
            throw InvalidTransition();
        }
    }

    private ServiceContractsDomainException InvalidTransition() =>
        new(ServiceContractsErrorCodes.ServiceInvalidTransition, $"The service is {Status} and cannot make that transition.");

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
