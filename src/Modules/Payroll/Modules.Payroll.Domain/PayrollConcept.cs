namespace Modules.Payroll.Domain;

/// <summary>
/// Un concepto propio de una empresa (R-11): horas extras con su multiplicador, ingreso con su clasificación sugerida, o deducción.
/// Los del sistema no son filas. El nombre es único por empresa y tipo, archivados incluidos, y se compara en su forma normalizada.
/// </summary>
public sealed class PayrollConcept
{
    private PayrollConcept()
    {
    }

    public PayrollConceptId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid CompanyId { get; private set; }

    /// <summary>Fijo desde que se crea: cambiarlo cambiaría lo ya capturado.</summary>
    public ConceptKind Kind { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    /// <summary>Sólo <c>Income</c>: la sugerencia de si constituye salario; nulo = sin sugerencia (quien captura elige).</summary>
    public bool? DefaultSalaryConstitutive { get; private set; }

    /// <summary>Sólo <c>Overtime</c>: el factor que multiplica el valor hora.</summary>
    public decimal? Multiplier { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool Archived => ArchivedAt is not null;

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PayrollConcept Create(
        PayrollConceptId id,
        Guid tenantId,
        Guid companyId,
        ConceptKind kind,
        string name,
        bool? defaultSalaryConstitutive,
        decimal? multiplier,
        DateTimeOffset now)
    {
        var concept = new PayrollConcept
        {
            Id = id,
            TenantId = tenantId,
            CompanyId = companyId,
            Kind = kind,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        concept.Apply(name, defaultSalaryConstitutive, multiplier);
        return concept;
    }

    public void Update(string name, bool? defaultSalaryConstitutive, decimal? multiplier, DateTimeOffset now)
    {
        Apply(name, defaultSalaryConstitutive, multiplier);
        Touch(now);
    }

    public void Archive(DateTimeOffset now)
    {
        if (Archived)
        {
            throw new PayrollDomainException(PayrollErrorCodes.ConceptAlreadyArchived, "The concept is already archived.");
        }

        ArchivedAt = now;
        Touch(now);
    }

    public void Unarchive(DateTimeOffset now)
    {
        if (!Archived)
        {
            throw new PayrollDomainException(PayrollErrorCodes.ConceptNotArchived, "The concept is not archived.");
        }

        ArchivedAt = null;
        Touch(now);
    }

    // El validador ya garantizó la forma del cuerpo: un multiplicador sólo con horas extras y una clasificación sólo con ingresos.
    private void Apply(string name, bool? defaultSalaryConstitutive, decimal? multiplier)
    {
        Name = name.Trim();
        NormalizedName = PayrollText.Fold(name)
            ?? throw new ArgumentException("A concept needs a name.", nameof(name));
        DefaultSalaryConstitutive = Kind == ConceptKind.Income ? defaultSalaryConstitutive : null;
        Multiplier = Kind == ConceptKind.Overtime ? multiplier : null;
    }

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
    }
}
