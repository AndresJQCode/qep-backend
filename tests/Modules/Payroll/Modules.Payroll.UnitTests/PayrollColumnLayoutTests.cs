using Modules.Payroll.Application;
using Modules.Payroll.Domain.Engine;

namespace Modules.Payroll.UnitTests;

/// <summary>El diseño de columnas del «Resumen de nómina» (contrato HTTP, «Excel»): el generador no calcula, sólo lee la fila.</summary>
public sealed class PayrollColumnLayoutTests
{
    private static readonly string[] Titles =
    [
        "Tipo de documento", "Número de documento", "Nombre", "Cargo", "Centro de trabajo", "Días", "Días de vacaciones", "Salario",
        "Vacaciones disfrutadas", "Auxilio",
        "Horas extras y recargos", "Otros ingresos salariales", "Ingresos no salariales", "Total devengado", "Salud", "Pensión", "FSP",
        "Otras deducciones", "Total deducciones", "Neto", "Salud empleador", "Pensión empleador", "ARL", "Caja", "ICBF", "SENA",
        "Cesantías", "Intereses a las cesantías", "Prima", "Vacaciones", "Costo empresa",
    ];

    // Each field of the summary holds its position in the contract (1 to 24, VacationPay is the second): a column that reads the wrong
    // field shows at once.
    private static readonly PayrollSummary Numbered = new(
        1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m, 11m, 12m, 13m, 14m, 15m, 16m, 17m, 18m, 19m, 20m, 21m, 22m, 23m, 24m);

    private static PayrollWorkbookRow Row(PayrollSummary? summary = null) =>
        new("CC", "1020304050", "Ana Pérez", "Analista", "Bodega - Principal", 15, 2, summary ?? Numbered);

    [Fact]
    public void TheSummaryHasTheThirtyOneColumnsInOrder()
    {
        var layout = PayrollColumnLayouts.Summary;

        Assert.Equal("summary", layout.Key);
        Assert.Equal("Resumen", layout.SheetName);
        Assert.Equal(31, layout.Columns.Count);
        Assert.Equal(Titles, layout.Columns.Select(column => column.Header));
    }

    [Fact]
    public void EveryMoneyColumnHasATotal()
    {
        var columns = PayrollColumnLayouts.Summary.Columns;

        var money = columns.Where(column => column.Format == PayrollColumnFormat.Money).ToList();
        Assert.Equal(24, money.Count);
        Assert.All(money, column => Assert.NotNull(column.Total));
        Assert.All(
            columns.Where(column => column.Format != PayrollColumnFormat.Money),
            column => Assert.Null(column.Total));
        Assert.Equal(PayrollColumnFormat.Integer, columns.Single(column => column.Header == "Días").Format);
        Assert.Equal(PayrollColumnFormat.Integer, columns.Single(column => column.Header == "Días de vacaciones").Format);
    }

    [Fact]
    public void ValuesComeFromTheSummary()
    {
        var columns = PayrollColumnLayouts.Summary.Columns;
        var row = Row();

        Assert.Equal("CC", columns[0].Value(row));
        Assert.Equal("1020304050", columns[1].Value(row));
        Assert.Equal("Ana Pérez", columns[2].Value(row));
        Assert.Equal("Analista", columns[3].Value(row));
        Assert.Equal("Bodega - Principal", columns[4].Value(row));
        Assert.Equal(15, columns[5].Value(row));
        Assert.Equal(2, columns[6].Value(row));

        var money = columns.Where(column => column.Format == PayrollColumnFormat.Money).ToList();
        Assert.Equal(
            Enumerable.Range(1, 24).Select(number => (object?)(decimal)number),
            money.Select(column => column.Value(row)));
        Assert.Equal(13m, columns.Single(column => column.Header == "Neto").Value(row));
        Assert.Equal(2m, columns.Single(column => column.Header == "Vacaciones disfrutadas").Value(row));
        Assert.Equal(
            Enumerable.Range(1, 24).Select(number => (decimal?)number),
            money.Select(column => column.Total!(Numbered)));
    }

    [Fact]
    public void ARowWithoutPositionOrWorkplaceReadsEmptyText()
    {
        var row = new PayrollWorkbookRow("CC", "1", "Ana", null, null, 15, 0, Numbered);

        var columns = PayrollColumnLayouts.Summary.Columns;

        Assert.Null(columns[3].Value(row));
        Assert.Null(columns[4].Value(row));
    }
}
