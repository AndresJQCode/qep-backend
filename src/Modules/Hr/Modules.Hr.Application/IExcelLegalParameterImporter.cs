using Modules.Hr.Domain;

namespace Modules.Hr.Application;

/// <summary>
/// Una fila de «Parámetros» tal como viene, sin ninguna validación de negocio: el texto crudo y recortado de cada celda (vacío ⇒
/// <c>null</c>). Las fechas y los números que Excel guarda como tales llegan como texto invariante (<c>yyyy-MM-dd</c>, punto
/// decimal). <c>RowNumber</c> es la fila real de la hoja (la 1 es el encabezado).
/// </summary>
public sealed record ExcelLegalParameterRow(int RowNumber, string? Key, string? EffectiveFrom, string? NewValue);

/// <summary>Una fila de «Tramos», con el mismo criterio que <see cref="ExcelLegalParameterRow"/>.</summary>
public sealed record ExcelLegalBracketRow(
    int RowNumber, string? Key, string? EffectiveFrom, string? LowerBound, string? UpperBound, string? Rate);

public sealed record LegalParameterWorkbook(
    IReadOnlyList<ExcelLegalParameterRow> Parameters, IReadOnlyList<ExcelLegalBracketRow> Brackets);

/// <summary>
/// Lee el Excel de valores legales (adaptador ClosedXML en Infrastructure). Sólo parseo estructural: busca las dos hojas por nombre y
/// las columnas por encabezado, sin mayúsculas ni tildes. Un archivo ilegible lanza <c>hr.legal_parameter.import.unreadable</c>; una
/// hoja o un encabezado que falta, <c>hr.legal_parameter.import.header_mismatch</c> (<c>HrDomainException</c> los dos). Salta las filas
/// en blanco.
/// </summary>
public interface IExcelLegalParameterImporter
{
    Task<LegalParameterWorkbook> ReadAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>Una clave en la plantilla: el valor que seguiría rigiendo y desde cuándo (nulos si la clave no tiene ninguna fila).</summary>
public sealed record LegalParameterTemplateRow(
    LegalParameterKey Key, string Name, LegalParameterUnit Unit, decimal? CurrentValue, DateOnly? CurrentFrom);

/// <summary>Lo que la plantilla lleva prellenado: las 30 claves y el conjunto de tramos que seguiría rigiendo en <c>NextYearStart</c>.</summary>
public sealed record LegalParameterTemplateData(
    DateOnly NextYearStart,
    IReadOnlyList<LegalParameterTemplateRow> Parameters,
    IReadOnlyList<LegalBracketInput> CurrentBrackets);

/// <summary>Arma la plantilla <c>.xlsx</c> (adaptador ClosedXML): «Parámetros» con las 30 claves y «Tramos» con el conjunto vigente.</summary>
public interface ILegalParameterTemplateBuilder
{
    byte[] Build(LegalParameterTemplateData data);
}
