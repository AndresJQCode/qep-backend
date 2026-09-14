namespace Modules.Quotations.Application;

/// <summary>
/// El lote de lectura y escritura que comparten <see cref="QuotationsExportProcessor"/> y
/// <see cref="OrdersExportProcessor"/> (D8): cada procesador trae su propio lector, ya cerrado
/// sobre sus filtros, y este método hace el resto —leer, resolver nombres/correos por lote,
/// escribir en streaming y calcular la clave del próximo lote—.
/// </summary>
internal static class ExportBatchLoop
{
    public static async Task<int> WriteAllAsync<TEntity, TCursor>(
        IExportWorkbook workbook,
        Func<TCursor?, int, CancellationToken, Task<IReadOnlyList<TEntity>>> readBatch,
        Func<IReadOnlyList<TEntity>, CancellationToken, Task<IEnumerable<ExportCell[]>>> toRows,
        Func<TEntity, TCursor> cursorOf,
        CancellationToken cancellationToken)
        where TCursor : class
    {
        var rowCount = 0;
        TCursor? after = null;
        while (true)
        {
            // Keyset (D8): el lote siguiente arranca después de la última fila leída, nunca un
            // offset que una fila nueva o que deja el filtro durante el export correría.
            var batch = await readBatch(after, ExportJobLimits.BatchSize, cancellationToken);

            if (batch.Count > 0)
            {
                // Nombres y correos por lote, no por fila: una ida cada mil filas.
                var rows = await toRows(batch, cancellationToken);
                foreach (var row in rows)
                {
                    workbook.AppendRow(row);
                }

                after = cursorOf(batch[^1]);
            }

            rowCount += batch.Count;
            if (batch.Count < ExportJobLimits.BatchSize)
            {
                break;
            }
        }

        return rowCount;
    }
}
