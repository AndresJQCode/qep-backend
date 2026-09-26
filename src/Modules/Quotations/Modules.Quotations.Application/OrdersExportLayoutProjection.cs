using System.Globalization;
using System.Text.RegularExpressions;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Aplica el layout efectivo del tenant a una fila del Excel de pedidos (spec 2026-09-24). El
/// processor sigue armando una celda por columna del catálogo, en su orden —cambio mínimo,
/// comprobable en unitaria—, y esto las reordena, descarta las ocultas e intercala las fijas en su
/// posición: como número cuando su valor es un número canónico, como texto si no (ver
/// <see cref="FixedCellFor"/>). Una llave repetida (ajuste 2026-09-25) toma la misma celda de origen cada
/// vez. Se arma una vez por job: el layout no cambia a mitad de un archivo.
/// </summary>
public sealed partial class OrdersExportLayoutProjection
{
    /// <summary>El ancho de una fija: no está en el catálogo, y 18 es el de "Cod. Producto", una
    /// columna corta de código, que es lo que una fija suele ser (spec 2026-09-24, "Processor").
    /// El ancho no depende de si la celda sale como número o como texto.</summary>
    public const double FixedColumnWidth = 18;

    // Por columna visible: el índice de la celda en el orden del catálogo, o -1 si es una fija.
    private readonly int[] _sources;

    // Las celdas de las fijas visibles, en su orden.
    private readonly ExportCell[] _fixedCells;

    private OrdersExportLayoutProjection(
        IReadOnlyList<ExportColumn> columns, int[] sources, ExportCell[] fixedCells)
    {
        Columns = columns;
        _sources = sources;
        _fixedCells = fixedCells;
    }

    /// <summary>Las columnas de la hoja: encabezado del tenant, ancho del catálogo (18 las fijas),
    /// sólo las visibles y en el orden del tenant.</summary>
    public IReadOnlyList<ExportColumn> Columns { get; }

    public static OrdersExportLayoutProjection For(IReadOnlyList<OrdersExportColumnSetting> effective)
    {
        var columns = new List<ExportColumn>(effective.Count);
        var sources = new List<int>(effective.Count);
        var fixedCells = new List<ExportCell>();

        foreach (var column in effective)
        {
            if (!column.Visible)
            {
                continue;
            }

            if (column.Kind == OrdersExportColumnKind.Fixed)
            {
                columns.Add(new ExportColumn(column.Header, FixedColumnWidth));
                sources.Add(-1);
                fixedCells.Add(FixedCellFor(column.Value ?? string.Empty));
                continue;
            }

            // Effective ya descartó toda llave que no esté en el catálogo: el índice existe.
            var index = OrdersExportColumnCatalog.IndexOf(column.Key!);
            columns.Add(new ExportColumn(column.Header, OrdersExportColumnCatalog.Columns[index].Width));
            sources.Add(index);
        }

        return new OrdersExportLayoutProjection(columns, [.. sources], [.. fixedCells]);
    }

    /// <summary>
    /// La celda de una fija (ajuste 2026-09-26). Un número canónico en cultura invariante
    /// —"0.19", "9999", "-1", "0", "901851609"— sale como número: un Excel con configuración
    /// regional colombiana lo muestra "0,19" y el ERP lo lee como cifra, mientras que como texto se
    /// quedaba "0.19". Todo lo demás sale como el texto que el tenant escribió: un cero a la
    /// izquierda es un código ("02"), una coma decimal es de otra cultura ("1,5"), y un "+" o un
    /// espacio son a propósito. El writer escribe los números con el separador invariante.
    /// </summary>
    public static ExportCell FixedCellFor(string value) =>
        CanonicalNumber().IsMatch(value)
            ? ExportCell.OfNumber(decimal.Parse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture))
            : ExportCell.OfText(value);

    // [0-9] y no \d: \d también acepta dígitos de otros alfabetos, que decimal.Parse no lee.
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalNumber();

    /// <summary>De las celdas de una fila en orden de catálogo a las de la hoja del tenant.</summary>
    public ExportCell[] Project(IReadOnlyList<ExportCell> catalogCells)
    {
        var cells = new ExportCell[_sources.Length];
        var nextFixed = 0;
        for (var position = 0; position < cells.Length; position++)
        {
            var source = _sources[position];
            cells[position] = source < 0 ? _fixedCells[nextFixed++] : catalogCells[source];
        }

        return cells;
    }
}
