namespace Modules.Hr.Application;

/// <summary>
/// Una fila del Excel de empleados, sin ninguna validación de negocio: el texto crudo y recortado de cada celda por encabezado de
/// <see cref="HrImportColumns"/> (vacío ⇒ <c>null</c>). Las fechas y los números que Excel guarda como tales llegan ya como texto
/// invariante (<c>yyyy-MM-dd</c>, punto decimal). <c>RowNumber</c> es la fila real del Excel (la 1 es el encabezado).
/// </summary>
public sealed record ExcelEmployeeRow(int RowNumber, IReadOnlyDictionary<string, string?> Cells);

/// <summary>
/// Lee el Excel de empleados (adaptador ClosedXML en Infrastructure). Sólo parseo estructural: lee la primera hoja y ubica las 36
/// columnas por su encabezado, sin mayúsculas ni tildes. Un archivo ilegible lanza <c>hr.import.unreadable</c>, uno al que le falta
/// un encabezado <c>hr.import.header_mismatch</c> (los dos, <c>HrDomainException</c>). Salta las filas en blanco.
/// </summary>
public interface IExcelEmployeeImporter
{
    Task<IReadOnlyList<ExcelEmployeeRow>> ReadAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>
/// Arma la plantilla <c>.xlsx</c> (adaptador ClosedXML): la hoja <c>Empleados</c> con los 36 encabezados y la hoja
/// <c>Valores válidos</c> con una columna por lista, más la validación de lista sobre las columnas que la admiten.
/// </summary>
public interface IEmployeeImportTemplateBuilder
{
    byte[] Build(HrSettingsIndex settings, HrCountryIndex countries);
}
