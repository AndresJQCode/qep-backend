using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.Application;

public enum PayrollColumnFormat
{
    Text,

    // The contract's name for a whole-number cell (days): CA1720 reads it as a type name.
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming", "CA1720:Identifier contains type name", Justification = "Name fixed by the plan's contract.")]
    Integer,
    Money,
}

/// <summary>
/// Una columna del Excel: su título, el formato de la celda, cómo lee su valor de una fila ya liquidada y, si es de dinero, cómo lee su
/// total del <see cref="PayrollSummary"/> del período. El generador no calcula: sólo escribe lo que estas funciones devuelven.
/// </summary>
public sealed record PayrollColumn(
    string Header,
    PayrollColumnFormat Format,
    Func<PayrollWorkbookRow, object?> Value,
    Func<PayrollSummary, decimal?>? Total);

/// <summary>El diseño de una hoja: las columnas en orden. El formato que pase el owner entra como otro diseño, sin tocar el motor ni el generador.</summary>
public sealed record PayrollColumnLayout(string Key, string SheetName, IReadOnlyList<PayrollColumn> Columns);

public static class PayrollColumnLayouts
{
    /// <summary>El «Resumen de nómina» inicial (spec, «Excel»): una fila por empleado y las 31 columnas del contrato HTTP.</summary>
    public static PayrollColumnLayout Summary { get; } = new(
        "summary",
        "Resumen",
        [
            Text("Tipo de documento", row => row.DocumentType),
            Text("Número de documento", row => row.DocumentNumber),
            Text("Nombre", row => row.FullName),
            Text("Cargo", row => row.Position),
            Text("Centro de trabajo", row => row.Workplace),
            new PayrollColumn("Días", PayrollColumnFormat.Integer, row => row.WorkedDays, null),
            new PayrollColumn("Días de vacaciones", PayrollColumnFormat.Integer, row => row.VacationDays, null),
            Money("Salario", summary => summary.Salary),
            Money("Vacaciones disfrutadas", summary => summary.VacationPay),
            Money("Auxilio", summary => summary.TransportAllowance),
            Money("Horas extras y recargos", summary => summary.Overtime),
            Money("Otros ingresos salariales", summary => summary.OtherSalaryIncome),
            Money("Ingresos no salariales", summary => summary.NonSalaryIncome),
            Money("Total devengado", summary => summary.TotalEarned),
            Money("Salud", summary => summary.HealthEmployee),
            Money("Pensión", summary => summary.PensionEmployee),
            Money("FSP", summary => summary.SolidarityFund),
            Money("Otras deducciones", summary => summary.OtherDeductions),
            Money("Total deducciones", summary => summary.TotalDeductions),
            Money("Neto", summary => summary.Net),
            Money("Salud empleador", summary => summary.HealthEmployer),
            Money("Pensión empleador", summary => summary.PensionEmployer),
            Money("ARL", summary => summary.Arl),
            Money("Caja", summary => summary.CompensationFund),
            Money("ICBF", summary => summary.Icbf),
            Money("SENA", summary => summary.Sena),
            // Order of the provisions: the spec's (severance, its interest, premium, vacation).
            Money("Cesantías", summary => summary.Severance),
            Money("Intereses a las cesantías", summary => summary.SeveranceInterest),
            Money("Prima", summary => summary.Premium),
            Money("Vacaciones", summary => summary.Vacation),
            Money("Costo empresa", summary => summary.EmployerCost),
        ]);

    private static PayrollColumn Text(string header, Func<PayrollWorkbookRow, string?> value) =>
        new(header, PayrollColumnFormat.Text, row => value(row), null);

    // One reader serves the row and the total: both come from a PayrollSummary.
    private static PayrollColumn Money(string header, Func<PayrollSummary, decimal> value) =>
        new(header, PayrollColumnFormat.Money, row => value(row.Summary), summary => value(summary));
}
