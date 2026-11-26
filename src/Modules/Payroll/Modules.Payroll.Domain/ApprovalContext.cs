namespace Modules.Payroll.Domain;

/// <summary>Lo que el período no sabe por sí solo y que decide si se puede aprobar: quien llama y lo que el caso de uso calculó.</summary>
/// <param name="DualControl">La configuración de la empresa.</param>
/// <param name="SourceChanged">Hr, las capturas o la configuración cambiaron desde que se liquidó (R-16).</param>
/// <param name="AnyNegativeNet">Algún empleado liquidado tiene neto negativo.</param>
/// <param name="FirstHalfApproved">La primera quincena del mes está aprobada o cerrada; no aplica en una primera quincena.</param>
public sealed record ApprovalContext(
    PayrollActor Caller,
    bool DualControl,
    bool SourceChanged,
    bool AnyNegativeNet,
    bool FirstHalfApproved);
