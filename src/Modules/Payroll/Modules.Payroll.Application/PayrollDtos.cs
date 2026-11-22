namespace Modules.Payroll.Application;

// DTO de respuesta (contrato HTTP, «DTOs»). Los enums viajan como el nombre exacto del miembro, en texto; los opcionales van
// siempre presentes como null. Los del período llegan con la Tarea 9, los de capturas y recurrentes con la 10.

public sealed record PayrollCompanyDto(Guid Id, string Name, string TaxId, bool IsActive);

/// <param name="System">Los del sistema no son filas: su id es fijo y no se editan ni se archivan.</param>
/// <param name="SystemKey">El nombre del <c>SystemConceptKey</c>; nulo en los propios.</param>
/// <param name="DefaultSalaryConstitutive">Sólo <c>Income</c>; nulo en los demás (y en un ingreso sin sugerencia).</param>
/// <param name="Multiplier">Sólo un <c>Overtime</c> propio; los del sistema salen de la tabla legal (nulo).</param>
/// <param name="Version">Nulo en los del sistema: no se editan.</param>
public sealed record PayrollConceptDto(
    Guid Id,
    string Kind,
    string Name,
    bool System,
    string? SystemKey,
    bool? DefaultSalaryConstitutive,
    decimal? Multiplier,
    bool Archived,
    long? Version);

/// <param name="Version">0 = nunca guardada: son los valores por defecto (R-17).</param>
/// <param name="Concepts">El catálogo: los del sistema primero en el orden de la tabla, luego los propios por tipo y nombre.</param>
public sealed record PayrollSettingsDto(
    PayrollCompanyDto Company,
    string TransportFrequency,
    string TransportCapBase,
    string OvertimeBase,
    bool Exonerated,
    bool DualControl,
    long Version,
    IReadOnlyList<PayrollConceptDto> Concepts);
