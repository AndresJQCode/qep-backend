namespace Modules.Payroll.Domain;

/// <summary>
/// Un ingreso o una deducción que se repite cada quincena en la ficha de un empleado de una empresa: <b>fijo</b> (el mismo valor) o
/// <b>proporcional</b> a los días, con un tope acumulado opcional. El estado <c>Exhausted</c> no se guarda: se calcula contra lo
/// aplicado en las últimas fotos (R-12), así que reabrir y reliquidar no deja un contador desincronizado.
/// </summary>
public sealed class RecurringConcept
{
    private RecurringConcept()
    {
    }

    public RecurringConceptId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    public Guid EmployeeId { get; private set; }

    /// <summary>Un concepto <c>Income</c> o <c>Deduction</c> del sistema o propio de la empresa: nunca <c>Overtime</c>.</summary>
    public Guid ConceptId { get; private set; }

    public RecurringMode Mode { get; private set; }

    public decimal Amount { get; private set; }

    /// <summary>Sólo en los ingresos: lo que se eligió al crearlo. Nulo en una deducción.</summary>
    public bool? SalaryConstitutive { get; private set; }

    public decimal? Cap { get; private set; }

    public bool Active { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static RecurringConcept Create(
        RecurringConceptId id,
        Guid tenantId,
        Guid companyId,
        Guid employeeId,
        Guid conceptId,
        RecurringMode mode,
        decimal amount,
        bool? salaryConstitutive,
        decimal? cap,
        DateTimeOffset now)
    {
        EnsureCap(amount, cap);
        return new RecurringConcept
        {
            Id = id,
            TenantId = tenantId,
            CompanyId = companyId,
            EmployeeId = employeeId,
            ConceptId = conceptId,
            Mode = mode,
            Amount = amount,
            SalaryConstitutive = salaryConstitutive,
            Cap = cap,
            Active = true,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>El concepto de la línea no se cambia: es otro recurrente. Cambian las condiciones.</summary>
    public void Update(RecurringMode mode, decimal amount, bool? salaryConstitutive, decimal? cap, DateTimeOffset now)
    {
        EnsureCap(amount, cap);
        Mode = mode;
        Amount = amount;
        SalaryConstitutive = salaryConstitutive;
        Cap = cap;
        Touch(now);
    }

    public void Activate(DateTimeOffset now)
    {
        if (Active)
        {
            throw new PayrollDomainException(PayrollErrorCodes.RecurringAlreadyActive, "The recurring concept is already active.");
        }

        Active = true;
        Touch(now);
    }

    public void Deactivate(DateTimeOffset now)
    {
        if (!Active)
        {
            throw new PayrollDomainException(PayrollErrorCodes.RecurringAlreadyInactive, "The recurring concept is already inactive.");
        }

        Active = false;
        Touch(now);
    }

    /// <summary>Inactivo si está apagado; agotado si hay tope y lo aplicado ya lo alcanzó; si no, activo (R-12).</summary>
    public RecurringStatus StatusFor(decimal accumulated)
    {
        if (!Active)
        {
            return RecurringStatus.Inactive;
        }

        return Cap is { } cap && accumulated >= cap ? RecurringStatus.Exhausted : RecurringStatus.Active;
    }

    // El validador de la solicitud responde 422 por campo; esto sólo defiende el invariante.
    private static void EnsureCap(decimal amount, decimal? cap)
    {
        if (cap is { } value && value < amount)
        {
            throw new ArgumentException("The cap of a recurring concept cannot be below its amount.", nameof(cap));
        }
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
