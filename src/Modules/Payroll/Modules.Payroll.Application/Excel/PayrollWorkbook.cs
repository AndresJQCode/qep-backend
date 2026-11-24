using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

/// <summary>Una fila ya liquidada del período. <c>Position</c> y <c>Workplace</c> van como Hr los dio al liquidar.</summary>
public sealed record PayrollWorkbookRow(
    string DocumentType,
    string DocumentNumber,
    string FullName,
    string? Position,
    string? Workplace,
    int WorkedDays,
    int VacationDays,
    PayrollSummary Summary);

public sealed record PayrollWorkbookExcluded(string DocumentType, string DocumentNumber, string FullName, string Reason);

/// <summary>
/// Todo lo que el generador necesita, ya calculado: nada se suma ni se redondea acá. <c>Totals</c> son los del backend (la suma de
/// los resultados guardados), no fórmulas del libro.
/// </summary>
/// <param name="GeneratedAtLocal">La hora del tenant, no UTC.</param>
public sealed record PayrollWorkbook(
    string CompanyName,
    string CompanyTaxId,
    DateOnly Start,
    DateOnly End,
    string HalfLabel,
    string StatusLabel,
    DateTimeOffset GeneratedAtLocal,
    IReadOnlyList<PayrollWorkbookRow> Rows,
    PayrollSummary Totals,
    IReadOnlyList<PayrollWorkbookExcluded> Excluded);

/// <summary>
/// El generador del Excel (adaptador ClosedXML en Infrastructure): el encabezado de la empresa, los títulos del diseño, una fila por
/// empleado, la fila <c>Totales</c> y la hoja <c>No liquidados</c>. Cambiar el formato es pasar otro <see cref="PayrollColumnLayout"/>.
/// </summary>
public interface IPayrollWorkbookBuilder
{
    byte[] Build(PayrollWorkbook workbook, PayrollColumnLayout layout);
}
