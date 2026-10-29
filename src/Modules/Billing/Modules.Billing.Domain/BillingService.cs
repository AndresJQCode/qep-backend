namespace Modules.Billing.Domain;

/// <summary>
/// Un servicio recurrente que el tenant le cobra a un cliente. Guarda la ancla
/// (<see cref="FirstDueDate"/>) y hasta qué vencimiento ya generó cobros
/// (<see cref="LastGeneratedDueDate"/>); los cobros en sí son <see cref="Charge"/>.
/// </summary>
public sealed class BillingService
{
    public const int NameMaxLength = 150;

    // EF Core materializa por acá; Create es el único punto de entrada de código.
    private BillingService()
    {
        Name = string.Empty;
        Currency = string.Empty;
    }

    public BillingServiceId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>FK blanda a Customers: Guid y no un id tipado, como en el resto de módulos.</summary>
    public Guid CustomerId { get; private set; }

    public string Name { get; private set; }

    public decimal Price { get; private set; }

    public string Currency { get; private set; }

    public BillingCycle Cycle { get; private set; }

    public DateOnly FirstDueDate { get; private set; }

    public BillingServiceStatus Status { get; private set; }

    /// <summary>Cuándo se reanudó por última vez; sirve para saltar los períodos pausados.</summary>
    public DateOnly? ReactivatedOn { get; private set; }

    public DateOnly? LastGeneratedDueDate { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static BillingService Create(
        BillingServiceId id,
        Guid tenantId,
        Guid customerId,
        string name,
        decimal price,
        string currency,
        BillingCycle cycle,
        DateOnly firstDueDate,
        DateTimeOffset now)
    {
        var (cleanName, cleanCurrency) = Validate(name, price, currency);
        return new BillingService
        {
            Id = id,
            TenantId = tenantId,
            CustomerId = customerId,
            Name = cleanName,
            Price = price,
            Currency = cleanCurrency,
            Cycle = cycle,
            FirstDueDate = firstDueDate,
            Status = BillingServiceStatus.Active,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Update(string name, decimal price, string currency, DateTimeOffset now)
    {
        if (Status == BillingServiceStatus.Cancelled)
        {
            throw new BillingDomainException(BillingErrorCodes.ServiceCancelled, "A cancelled service cannot be edited.");
        }

        var (cleanName, cleanCurrency) = Validate(name, price, currency);
        Name = cleanName;
        Price = price;
        Currency = cleanCurrency;
        Touch(now);
    }

    public void Pause(DateTimeOffset now)
    {
        RequireStatus(BillingServiceStatus.Active);
        Status = BillingServiceStatus.Paused;
        Touch(now);
    }

    public void Resume(DateOnly today, DateTimeOffset now)
    {
        RequireStatus(BillingServiceStatus.Paused);
        Status = BillingServiceStatus.Active;
        ReactivatedOn = today;
        Touch(now);
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status == BillingServiceStatus.Cancelled)
        {
            throw InvalidTransition();
        }

        Status = BillingServiceStatus.Cancelled;
        Touch(now);
    }

    /// <summary>
    /// El próximo vencimiento a generar (spec §3.2), o null si el servicio no está activo. Tras una
    /// reanudación se salta lo que cayó en la pausa: la primera ancla igual o posterior a la fecha
    /// de reactivación.
    /// </summary>
    public DateOnly? NextDueDateToGenerate()
    {
        if (Status != BillingServiceStatus.Active)
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

    private static (string Name, string Currency) Validate(string name, decimal price, string currency)
    {
        var cleanName = name?.Trim() ?? string.Empty;
        if (cleanName.Length is 0 or > NameMaxLength)
        {
            throw new BillingDomainException(BillingErrorCodes.ServiceInvalid, $"The name must have between 1 and {NameMaxLength} characters.");
        }

        if (price <= 0)
        {
            throw new BillingDomainException(BillingErrorCodes.ServiceInvalid, "The price must be greater than zero.");
        }

        var cleanCurrency = currency?.Trim() ?? string.Empty;
        if (cleanCurrency.Length == 0)
        {
            throw new BillingDomainException(BillingErrorCodes.ServiceInvalid, "The currency is required.");
        }

        return (cleanName, cleanCurrency);
    }

    private void RequireStatus(BillingServiceStatus expected)
    {
        if (Status != expected)
        {
            throw InvalidTransition();
        }
    }

    private BillingDomainException InvalidTransition() =>
        new(BillingErrorCodes.ServiceInvalidTransition, $"The service is {Status} and cannot make that transition.");

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
