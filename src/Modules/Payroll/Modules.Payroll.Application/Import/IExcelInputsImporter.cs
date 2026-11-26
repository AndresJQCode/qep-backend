namespace Modules.Payroll.Application;

/// <summary>
/// Una fila del Excel de novedades, sin ninguna validación de negocio: el texto crudo y recortado de cada celda por encabezado de
/// <see cref="InputsImportColumns"/> (vacío ⇒ <c>null</c>). Los números llegan en texto invariante (punto decimal).
/// <c>RowNumber</c> es la fila real del Excel (la 1 es el encabezado).
/// </summary>
public sealed record ExcelInputsRow(int RowNumber, IReadOnlyDictionary<string, string?> Cells);

/// <summary>
/// Lee el Excel de novedades (adaptador ClosedXML en Infrastructure). Sólo parseo estructural: lee la hoja <c>Novedades</c> (o la
/// primera) y ubica las columnas por su encabezado. Un archivo ilegible lanza <c>payroll.import.unreadable</c>, uno al que le falta un
/// encabezado <c>payroll.import.header_mismatch</c> (<c>PayrollDomainException</c>). Salta las filas en blanco.
/// </summary>
public interface IExcelInputsImporter
{
    Task<IReadOnlyList<ExcelInputsRow>> ReadAsync(Stream content, CancellationToken cancellationToken);
}

public sealed record InputsTemplateEmployee(string DocumentNumber, string FullName);

/// <param name="Employees">Los de la quincena que se pueden capturar (liquidables y bloqueados): hoja <c>Empleados</c>.</param>
/// <param name="ConceptNames">Los conceptos no archivados de la empresa, del sistema y propios.</param>
public sealed record InputsImportTemplateData(
    IReadOnlyList<InputsTemplateEmployee> Employees, IReadOnlyList<string> ConceptNames);

/// <summary>Arma la plantilla <c>.xlsx</c> (adaptador ClosedXML): <c>Novedades</c>, <c>Empleados</c> y <c>Valores válidos</c>.</summary>
public interface IInputsImportTemplateBuilder
{
    byte[] Build(InputsImportTemplateData data);
}
