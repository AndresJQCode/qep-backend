namespace Modules.Payroll.Domain;

/// <summary>
/// Unas vacaciones disfrutadas de un empleado en una empresa, fuera de cualquier quincena. La quincena que las paga (<c>Qep</c>) no se
/// guarda: es la que contiene <see cref="StartDate"/>, exista o no todavía. <see cref="BusinessDays"/> y <see cref="ReturnDate"/> se
/// guardan y no se recalculan: si Hr cambia después los días de descanso, lo registrado no cambia hasta que se edite.
/// </summary>
public sealed class Vacation
{
    private Vacation()
    {
    }

    public VacationId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid EmployeeId { get; private set; }

    public VacationOrigin Origin { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly EndDate { get; private set; }

    public DateOnly ReturnDate { get; private set; }

    /// <summary><c>Qep</c>: calculados al guardar. <c>External</c>: los tecleados, los de Siigo; sólo se muestran.</summary>
    public int BusinessDays { get; private set; }

    /// <summary>IBC del mes anterior al inicio, tecleado. Obligatorio con <c>External</c>; con <c>Qep</c> sólo si QEP no lo tiene.</summary>
    public decimal? PreviousMonthIbc { get; private set; }

    public IReadOnlyList<ExternalVacationMonth> ExternalMonths { get; private set; } = [];

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Vacation Create(
        VacationId id,
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        VacationOrigin origin,
        DateOnly startDate,
        DateOnly endDate,
        int businessDays,
        DateOnly returnDate,
        decimal? previousMonthIbc,
        IReadOnlyList<ExternalVacationMonth> externalMonths,
        DateTimeOffset now)
    {
        Ensure(origin, startDate, endDate, businessDays, previousMonthIbc, externalMonths);
        return new Vacation
        {
            Id = id,
            TenantId = tenantId,
            CompanyId = companyId,
            EmployeeId = employeeId,
            Origin = origin,
            StartDate = startDate,
            EndDate = endDate,
            BusinessDays = businessDays,
            ReturnDate = returnDate,
            PreviousMonthIbc = previousMonthIbc,
            ExternalMonths = [.. externalMonths],
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Cambiar el origen está permitido: las mismas reglas de bloqueo del handler lo cubren.</summary>
    public void Update(
        VacationOrigin origin,
        DateOnly startDate,
        DateOnly endDate,
        int businessDays,
        DateOnly returnDate,
        decimal? previousMonthIbc,
        IReadOnlyList<ExternalVacationMonth> externalMonths,
        DateTimeOffset now)
    {
        Ensure(origin, startDate, endDate, businessDays, previousMonthIbc, externalMonths);
        Origin = origin;
        StartDate = startDate;
        EndDate = endDate;
        BusinessDays = businessDays;
        ReturnDate = returnDate;
        PreviousMonthIbc = previousMonthIbc;
        ExternalMonths = [.. externalMonths];
        Version++;
        UpdatedAt = now;
    }

    /// <summary>Inclusive on both ends: a range that shares even one day overlaps.</summary>
    public bool Overlaps(DateOnly start, DateOnly end) => StartDate <= end && EndDate >= start;

    // El validador de la solicitud responde 422 por campo; esto sólo defiende los invariantes.
    private static void Ensure(
        VacationOrigin origin,
        DateOnly startDate,
        DateOnly endDate,
        int businessDays,
        decimal? previousMonthIbc,
        IReadOnlyList<ExternalVacationMonth> externalMonths)
    {
        ArgumentNullException.ThrowIfNull(externalMonths);
        if (endDate < startDate)
        {
            throw new ArgumentException("A vacation cannot end before it starts.", nameof(endDate));
        }

        if (origin == VacationOrigin.External)
        {
            if (previousMonthIbc is null)
            {
                throw new ArgumentException("An external vacation needs the IBC of the previous month.", nameof(previousMonthIbc));
            }

            if (businessDays < 1)
            {
                throw new ArgumentException("An external vacation needs at least one business day.", nameof(businessDays));
            }
        }
        else if (externalMonths.Count > 0)
        {
            throw new ArgumentException("Only an external vacation carries typed months.", nameof(externalMonths));
        }
    }
}
