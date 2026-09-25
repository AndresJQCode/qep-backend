using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Aplica el layout efectivo del tenant a una fila del Excel de pedidos (spec 2026-09-24). El
/// processor sigue armando las 33 celdas en el orden del catálogo —cambio mínimo, comprobable en
/// unitaria—, y esto las reordena, descarta las ocultas e intercala las fijas como texto en su
/// posición. Se arma una vez por job: el layout no cambia a mitad de un archivo.
/// </summary>
public sealed class OrdersExportLayoutProjection
{
    /// <summary>El ancho de una fija: no está en el catálogo, y 18 es el de "Cod. Producto", una
    /// columna corta de código, que es lo que una fija suele ser (spec 2026-09-24, "Processor").</summary>
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
                fixedCells.Add(ExportCell.OfText(column.Value));
                continue;
            }

            // Effective ya descartó toda llave que no esté en el catálogo: el índice existe.
            var index = OrdersExportColumnCatalog.IndexOf(column.Key!);
            columns.Add(new ExportColumn(column.Header, OrdersExportColumnCatalog.Columns[index].Width));
            sources.Add(index);
        }

        return new OrdersExportLayoutProjection(columns, [.. sources], [.. fixedCells]);
    }

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
