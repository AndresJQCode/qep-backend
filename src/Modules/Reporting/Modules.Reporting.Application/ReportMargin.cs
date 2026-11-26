namespace Modules.Reporting.Application;

/// <summary>
/// El margen bruto % del reporte de margen (spec 2026-11-10 §6.3): utilidad ÷ venta neta × 100.
///
/// Se calcula acá y no en el SQL porque es una división de dos sumas que la base ya hizo, y así tiene
/// prueba unitaria. El redondeo es lejos del cero, el mismo de <c>round()</c> de PostgreSQL: un 66,65
/// no puede dar 66,6 en la pantalla y 66,7 en una consulta a mano.
/// </summary>
public static class ReportMargin
{
    /// <summary>Nulo sin venta neta: un margen sobre cero no existe, y un 0 % o un 100 % lo inventaría.</summary>
    public static decimal? Percent(decimal netSales, decimal grossProfit) =>
        netSales == 0m
            ? null
            : Math.Round(grossProfit / netSales * 100m, 1, MidpointRounding.AwayFromZero);
}
