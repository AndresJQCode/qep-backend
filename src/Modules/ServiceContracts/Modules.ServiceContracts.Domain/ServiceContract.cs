namespace Modules.ServiceContracts.Domain;

/// <summary>
/// Un servicio recurrente que el tenant le cobra a un cliente. Guarda la ancla
/// (<see cref="FirstDueDate"/>) y hasta qué período ya generó cobros
/// (<see cref="LastGeneratedPeriodDate"/>); los cobros en sí son <see cref="Charge"/>.
/// </summary>
public sealed class ServiceContract
{
    public const int NameMaxLength = 150;

    public const int DescriptionMaxLength = 1000;

    public const decimal MaxIpcExtraPoints = 50m;

    private const int UpcomingPeriodsLimit = 12;

    private readonly List<DateOnly> _carriedHeldPeriods = [];

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

    /// <summary>El último período generado. La columna conserva su nombre desplegado, <c>last_generated_due_date</c>.</summary>
    public DateOnly? LastGeneratedPeriodDate { get; private set; }

    public AdjustmentMode AdjustmentMode { get; private set; }

    /// <summary>Los puntos que se suman al IPC; 0 en <c>Manual</c>.</summary>
    public decimal IpcExtraPoints { get; private set; }

    /// <summary>Desde cuándo se revisa el precio (spec reajuste §5).</summary>
    public DateOnly NextReviewDate { get; private set; }

    /// <summary>Desde cuándo está retenido el cobro de la revisión; nulo si no lo está.</summary>
    public DateOnly? HeldSince { get; private set; }

    /// <summary>
    /// Los períodos que estaban retenidos al pausar y siguen pendientes (spec reajuste §6.4): en orden y siempre posteriores a
    /// <see cref="LastGeneratedPeriodDate"/>. Son lo primero que el cursor de períodos recorre; lo que cayó en la pausa no está.
    /// </summary>
    public IReadOnlyList<DateOnly> CarriedHeldPeriods => _carriedHeldPeriods;

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
            // Provisional hasta que InitializePriceReview la fije, después de la generación del alta.
            AdjustmentMode = AdjustmentMode.Manual,
            IpcExtraPoints = ToMoney(0m),
            NextReviewDate = firstDueDate.AddMonths(12),
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
            : ServicePriceChange.Record(
                this, previousPrice, previousCurrency, reason, changedByMemberId, PriceChangeOrigin.Edit, null, now);
    }

    /// <summary>
    /// El reajuste con que nace un contrato creado o importado. Se llama <b>después</b> de generar: la revisión por defecto
    /// depende del último período generado. No toca <see cref="Version"/>, que en el alta sigue siendo 1.
    /// </summary>
    public void InitializePriceReview(AdjustmentMode mode, decimal ipcExtraPoints, DateOnly? nextReviewDate) =>
        SetPriceReview(mode, ipcExtraPoints, nextReviewDate ?? DefaultNextReviewDate());

    /// <summary>
    /// La edición del reajuste; se permite en pausa. No libera una retención: eso lo decide la generación que quien llama corre
    /// después, en la misma transacción.
    /// </summary>
    public void UpdatePriceReview(AdjustmentMode mode, decimal ipcExtraPoints, DateOnly nextReviewDate, DateTimeOffset now)
    {
        EnsureNotCancelled();
        SetPriceReview(mode, ipcExtraPoints, nextReviewDate);
        Touch(now);
    }

    /// <summary>El primer aniversario del ancla posterior a lo último generado (o al ancla si no generó nada).</summary>
    public DateOnly DefaultNextReviewDate()
    {
        var after = LastGeneratedPeriodDate ?? FirstDueDate;
        var years = 1;
        while (FirstDueDate.AddMonths(12 * years) <= after)
        {
            years++;
        }

        return FirstDueDate.AddMonths(12 * years);
    }

    /// <summary>
    /// La regla única de «la revisión quedó resuelta» (spec reajuste §5): suma los años enteros que hagan falta para quedar
    /// después de <paramref name="resolved"/>, en una sola suma para no perder el día elegido (29 de febrero). Tras una pausa
    /// larga queda una sola revisión, no una por cada año saltado.
    /// </summary>
    public void AdvanceReview(DateOnly resolved)
    {
        var years = 1;
        while (NextReviewDate.AddMonths(12 * years) <= resolved)
        {
            years++;
        }

        NextReviewDate = NextReviewDate.AddMonths(12 * years);
    }

    /// <summary>«Mantener precio»: resuelve la revisión vigente sin tocar el precio.</summary>
    public void KeepPrice(DateTimeOffset now)
    {
        EnsureCanChangePrice();
        AdvanceReview(PriceReview.ReviewPeriodOf(this));
        Touch(now);
    }

    /// <summary>
    /// Los próximos períodos sin generar, para elegir desde cuándo programar. En <c>Ipc</c> sólo hasta la revisión inclusive:
    /// un cambio posterior violaría <see cref="PriceReview.EnsureNotAfterReview"/>.
    /// </summary>
    public IReadOnlyList<DateOnly> UpcomingPeriods()
    {
        if (NextPeriodToGenerate() is not { } period)
        {
            return [];
        }

        var last = AdjustmentMode == AdjustmentMode.Ipc ? PriceReview.ReviewPeriodOf(this) : DateOnly.MaxValue;
        var periods = new List<DateOnly>(UpcomingPeriodsLimit);
        while (periods.Count < UpcomingPeriodsLimit && period <= last)
        {
            periods.Add(period);
            period = PeriodAfter(period);
        }

        return periods;
    }

    /// <summary>
    /// Pausa el contrato. Si estaba retenido, guarda lo retenido: todo período sin generar cuyo día de generación
    /// (<c>período − 5</c>) es igual o anterior a <paramref name="today"/>, incluido el de la ventana que abrió ese mismo día
    /// aunque la corrida todavía no haya pasado. Es lo mismo que cobraría una liberación ese día.
    /// </summary>
    /// <param name="today">La «hoy» del tenant.</param>
    public void Pause(DateOnly today, DateTimeOffset now)
    {
        RequireStatus(ServiceContractStatus.Active);
        if (HeldSince is not null)
        {
            var limit = today.AddDays(ChargeGenerator.WindowDays);
            for (var period = NextUngeneratedPeriod(); period <= limit; period = PeriodAfter(period))
            {
                // El cursor recorre primero lo ya arrastrado, así que lo que se agrega queda al final y en orden.
                if (!_carriedHeldPeriods.Contains(period))
                {
                    _carriedHeldPeriods.Add(period);
                }
            }
        }

        Status = ServiceContractStatus.Paused;
        HeldSince = null;
        StatusChangedAt = now;
        Touch(now);
    }

    /// <summary>
    /// Reanuda: lo que cayó en la pausa no se cobra nunca, pero lo que ya estaba retenido sigue retenido desde hoy. Si deja de
    /// ser de revisión lo decide la generación que quien llama corre después, en la misma transacción.
    /// </summary>
    public void Resume(DateOnly today, DateTimeOffset now)
    {
        RequireStatus(ServiceContractStatus.Paused);
        Status = ServiceContractStatus.Active;
        ReactivatedOn = today;
        if (_carriedHeldPeriods.Count > 0)
        {
            HeldSince = today;
        }

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
        HeldSince = null;
        _carriedHeldPeriods.Clear();
        StatusChangedAt = now;
        Touch(now);
    }

    /// <summary>El próximo período a generar (spec §3.2), o null si el servicio no está activo.</summary>
    public DateOnly? NextPeriodToGenerate() =>
        Status == ServiceContractStatus.Active ? NextUngeneratedPeriod() : null;

    /// <summary>
    /// El primer período sin generar, mire o no el estado: lo necesitan la revisión de un contrato pausado y la invariante del
    /// reajuste. Primero los retenidos que una pausa dejó pendientes; agotados, tras una reanudación se salta lo que cayó en la
    /// pausa: la primera ancla igual o posterior a la fecha de reactivación.
    /// </summary>
    public DateOnly NextUngeneratedPeriod() =>
        LastGeneratedPeriodDate is { } last ? PeriodAfter(last) : FirstDueDate;

    /// <summary>
    /// El primer período sin generar <c>&gt;= date</c>, por el mismo recorrido que <see cref="NextUngeneratedPeriod"/>: nunca
    /// responde un período que cayó en una pausa antes de los retenidos que arrastra.
    /// </summary>
    public DateOnly FirstUngeneratedPeriodOnOrAfter(DateOnly date)
    {
        foreach (var carried in _carriedHeldPeriods)
        {
            if (carried >= date)
            {
                return carried;
            }
        }

        var start = _carriedHeldPeriods.Count > 0 ? PeriodAfter(_carriedHeldPeriods[^1]) : NextUngeneratedPeriod();
        return start >= date ? start : BillingCycles.FirstDueDateOnOrAfter(FirstDueDate, Cycle, date);
    }

    public void MarkGenerated(DateOnly periodDate)
    {
        _carriedHeldPeriods.Remove(periodDate);
        LastGeneratedPeriodDate = periodDate;
    }

    /// <summary>La retención desde <paramref name="today"/>; cuándo es nueva y cuándo se conserva lo decide la generación.</summary>
    internal void Hold(DateOnly today) => HeldSince = today;

    internal void ReleaseHold() => HeldSince = null;

    /// <summary>
    /// Aplica un cambio programado en <paramref name="period"/>: el contrato toma el precio, el programado queda aplicado y se
    /// devuelve la fila del historial, firmada por quien lo programó. Como la generación, no es una edición:
    /// <see cref="UpdatedAt"/> no se toca y la versión la sube quien guarda.
    /// </summary>
    internal ServicePriceChange ApplyScheduledPrice(ServicePriceSchedule schedule, DateOnly period, DateTimeOffset now)
    {
        schedule.Apply(period, now);
        var previousPrice = Price;
        Price = ToMoney(schedule.NewPrice);
        return ServicePriceChange.Record(
            this,
            previousPrice,
            Currency,
            schedule.Reason,
            schedule.CreatedByMemberId,
            schedule.Source == PriceScheduleSource.Ipc ? PriceChangeOrigin.Ipc : PriceChangeOrigin.Scheduled,
            schedule.Id,
            now);
    }

    /// <summary>
    /// Lo llama la corrida diaria cuando la generación cambió un servicio ya guardado (generó cobros, retuvo, movió la revisión
    /// o aplicó un cambio programado). Sin esto, una cancelación que cargó el servicio
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
    internal static decimal ToMoney(decimal value) => decimal.Round(value, 2) + 0.00m;

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

    /// <summary>El motivo de un cambio de precio, ya recortado; lo comparten la edición y el cambio programado.</summary>
    internal static string ValidateReason(string? reason)
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

    /// <summary>Programar un precio y mantenerlo exigen un contrato activo; cancelado y pausado se distinguen por su código.</summary>
    internal void EnsureCanChangePrice()
    {
        EnsureNotCancelled();
        if (Status != ServiceContractStatus.Active)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ServiceNotActive, "The service is not active.");
        }
    }

    /// <summary>
    /// El período que sigue a <paramref name="period"/> entre los que se cobran: el próximo retenido arrastrado y, sin más, la
    /// siguiente ancla, saltando las que cayeron antes de la última reanudación.
    /// </summary>
    private DateOnly PeriodAfter(DateOnly period)
    {
        foreach (var carried in _carriedHeldPeriods)
        {
            if (carried > period)
            {
                return carried;
            }
        }

        return ReactivatedOn is { } reactivated && reactivated > period
            ? BillingCycles.FirstDueDateOnOrAfter(FirstDueDate, Cycle, reactivated)
            : BillingCycles.NextDueDate(FirstDueDate, Cycle, period);
    }

    private void EnsureNotCancelled()
    {
        if (Status == ServiceContractStatus.Cancelled)
        {
            throw new ServiceContractsDomainException(ServiceContractsErrorCodes.ServiceCancelled, "A cancelled service cannot be edited.");
        }
    }

    private void SetPriceReview(AdjustmentMode mode, decimal ipcExtraPoints, DateOnly nextReviewDate)
    {
        if (ipcExtraPoints is < 0 or > MaxIpcExtraPoints)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.ServiceInvalid, $"The IPC extra points must be between 0 and {MaxIpcExtraPoints}.");
        }

        if (LastGeneratedPeriodDate is { } last && nextReviewDate <= last)
        {
            throw new ServiceContractsDomainException(
                ServiceContractsErrorCodes.ServiceReviewDateInvalid, "The next review date must be after the last generated period.");
        }

        AdjustmentMode = mode;
        IpcExtraPoints = ToMoney(mode == AdjustmentMode.Ipc ? ipcExtraPoints : 0m);
        NextReviewDate = nextReviewDate;
    }

    // Internal porque programar y cancelar un cambio de precio también cuentan como editar el contrato.
    internal void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
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
}
