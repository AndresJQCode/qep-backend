using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Domain;

/// <summary>
/// La configuración de nómina de una empresa. Mientras nunca se guarda no hay fila: la pantalla recibe los valores por defecto con
/// <c>version: 0</c> y el primer <c>PUT</c> va con <c>If-Match: "0"</c> (R-17). Cada <see cref="Update"/> sube la versión.
/// </summary>
public sealed class CompanyPayrollSettings
{
    private CompanyPayrollSettings()
    {
    }

    public CompanyPayrollSettingsId Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>FK real a <c>companies.companies</c>, agregada a mano en la migración (R-19): fija desde que se crea.</summary>
    public Guid CompanyId { get; private set; }

    public TransportFrequency TransportFrequency { get; private set; }

    public TransportCapBase TransportCapBase { get; private set; }

    public OvertimeBase OvertimeBase { get; private set; }

    public bool Exonerated { get; private set; }

    /// <summary>Con doble control, quien liquidó por última vez no aprueba el período.</summary>
    public bool DualControl { get; private set; }

    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Lo que rige mientras la empresa nunca guardó su configuración (spec, «Configuración de nómina por empresa»).</summary>
    public static EngineSettings DefaultsFor() =>
        new(TransportFrequency.Biweekly, TransportCapBase.SalaryAndSalaryIncome, OvertimeBase.SalaryOnly, Exonerated: true);

    public const bool DefaultDualControl = false;

    public static CompanyPayrollSettings Create(
        CompanyPayrollSettingsId id,
        Guid tenantId,
        Guid companyId,
        TransportFrequency transportFrequency,
        TransportCapBase transportCapBase,
        OvertimeBase overtimeBase,
        bool exonerated,
        bool dualControl,
        DateTimeOffset now) => new()
        {
            Id = id,
            TenantId = tenantId,
            CompanyId = companyId,
            TransportFrequency = transportFrequency,
            TransportCapBase = transportCapBase,
            OvertimeBase = overtimeBase,
            Exonerated = exonerated,
            DualControl = dualControl,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };

    public void Update(
        TransportFrequency transportFrequency,
        TransportCapBase transportCapBase,
        OvertimeBase overtimeBase,
        bool exonerated,
        bool dualControl,
        DateTimeOffset now)
    {
        TransportFrequency = transportFrequency;
        TransportCapBase = transportCapBase;
        OvertimeBase = overtimeBase;
        Exonerated = exonerated;
        DualControl = dualControl;
        Version++;
        UpdatedAt = now;
    }

    public EngineSettings ToEngine() => new(TransportFrequency, TransportCapBase, OvertimeBase, Exonerated);
}
