namespace Modules.Payroll.Application;

// Todo es anulable para que el validador devuelva el campo y no un 400 de binding. Los enums llegan como texto: el nombre exacto
// del miembro («Biweekly»), no su etiqueta ni su número.

/// <summary><c>PUT {c}/settings</c>: reemplaza todo.</summary>
public sealed record PayrollSettingsRequest(
    string? TransportFrequency,
    string? TransportCapBase,
    string? OvertimeBase,
    bool? Exonerated,
    bool? DualControl);

/// <summary>
/// <c>POST {c}/concepts</c> y <c>PUT {c}/concepts/{id}</c>. <c>Multiplier</c>: requerido en <c>Overtime</c> (0 &lt; m ≤ 10, ≤ 4
/// decimales) y nulo en los demás. <c>DefaultSalaryConstitutive</c>: sólo <c>Income</c>; en otro tipo debe ser nulo. En el
/// <c>PUT</c>, <c>Kind</c> es el guardado: el tipo no se cambia.
/// </summary>
public sealed record ConceptRequest(string? Kind, string? Name, bool? DefaultSalaryConstitutive, decimal? Multiplier);
