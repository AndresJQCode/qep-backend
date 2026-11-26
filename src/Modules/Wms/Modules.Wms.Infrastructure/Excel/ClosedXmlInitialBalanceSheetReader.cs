using System.Globalization;
using System.IO.Compression;
using ClosedXML.Excel;
using Modules.Wms.Application;
using Modules.Wms.Domain;

namespace Modules.Wms.Infrastructure.Excel;

/// <summary>
/// Lee la hoja de carga inicial con ClosedXML. Sólo parseo estructural: ubica las seis columnas por su cabecera (no por posición) y
/// devuelve cada fila como texto crudo. Las celdas numéricas y de fecha se vuelven texto invariante (<c>12.5</c>, <c>2027-03-31</c>)
/// para que la regla de la fila no dependa de la cultura de quien guardó el archivo.
/// </summary>
internal sealed class ClosedXmlInitialBalanceSheetReader : IInitialBalanceSheetReader
{
    /// <summary>Lo que se admite subir, ya comprimido. Es el tope del handler (5 MB), repetido acá porque <c>Length</c> lo declara el
    /// cliente y el lector mide lo que de verdad lee.</summary>
    public const int MaxCompressedBytes = 5 * 1024 * 1024;

    /// <summary>Lo que puede ocupar el archivo descomprimido. Una hoja de 500 filas pesa unas decenas de KB y una de 100 000 celdas de
    /// texto, pocos MB: 20 MB deja un margen amplio sobre cualquier carga legítima y corta antes de que ClosedXML materialice cientos
    /// de MB (un .xlsx de 1 MB comprimido llegó a 1,5 GB al abrirse).</summary>
    public const int MaxUncompressedBytes = 20 * 1024 * 1024;

    /// <summary>Un .xlsx real trae una docena de partes; este tope frena los zips con miles de entradas vacías.</summary>
    public const int MaxEntries = 100;

    public InitialBalanceSheet Parse(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Antes de ClosedXML y fuera del try: el rechazo es un 422 con su código, no una hoja «sin columnas».
        var buffered = BufferWithinLimit(content, cancellationToken);
        EnsureWithinUncompressedLimit(buffered, cancellationToken);

        try
        {
            using var workbook = new XLWorkbook(buffered);
            return ParseWorksheet(workbook.Worksheets.First(), cancellationToken);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            // Un archivo corrupto, protegido o que no es un .xlsx revienta ClosedXML con excepciones de bajo nivel que no le dicen nada a
            // quien lo subió: desde afuera es indistinguible de una cabecera equivocada. Un OutOfMemoryException no se traga: dejarlo
            // pasar es lo único honesto cuando el proceso ya no tiene memoria.
            return new InitialBalanceSheet(false, []);
        }
    }

    private static WmsDomainException InvalidFile(string reason) => new(WmsErrorCodes.ImportInvalidFile, reason);

    // Se lee hasta el tope más un byte: el Length de la petición lo declara el cliente.
    private static MemoryStream BufferWithinLimit(Stream content, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length + read > MaxCompressedBytes)
            {
                throw InvalidFile("El archivo pesa más de 5 MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    // Dos medidas: los tamaños que declara el directorio central (barato, rechaza lo evidente) y los bytes que de verdad salen al
    // descomprimir cada parte (los declarados se pueden falsear, y es lo que ClosedXML va a tener que materializar).
    private static void EnsureWithinUncompressedLimit(MemoryStream file, CancellationToken cancellationToken)
    {
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > MaxEntries)
            {
                throw InvalidFile("El archivo no es una hoja de cálculo válida.");
            }

            long declared = 0;
            foreach (var entry in zip.Entries)
            {
                declared += entry.Length;
                if (declared > MaxUncompressedBytes)
                {
                    throw InvalidFile("El archivo descomprimido es demasiado grande para una carga inicial.");
                }
            }

            long actual = 0;
            var chunk = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    actual += read;
                    if (actual > MaxUncompressedBytes)
                    {
                        throw InvalidFile("El archivo descomprimido es demasiado grande para una carga inicial.");
                    }
                }
            }
        }
        catch (InvalidDataException)
        {
            throw InvalidFile("El archivo no es una hoja de cálculo válida.");
        }
        finally
        {
            file.Position = 0;
        }
    }

    private static InitialBalanceSheet ParseWorksheet(IXLWorksheet worksheet, CancellationToken cancellationToken)
    {
        var columns = MapHeaderColumns(worksheet);
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var expected in InitialBalanceColumns.Ordered)
        {
            if (!columns.TryGetValue(expected, out var index))
            {
                return new InitialBalanceSheet(false, []);
            }

            indexes[expected] = index;
        }

        var rows = new List<InitialBalanceSheetRow>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = worksheet.Row(rowNumber);
            var code = Text(row, indexes[InitialBalanceColumns.Code]);
            var location = Text(row, indexes[InitialBalanceColumns.Location]);
            var lot = Text(row, indexes[InitialBalanceColumns.Lot]);
            var expiration = Text(row, indexes[InitialBalanceColumns.Expiration]);
            var quantity = Text(row, indexes[InitialBalanceColumns.Quantity]);
            var uom = Text(row, indexes[InitialBalanceColumns.Uom]);

            // Una fila completamente vacía es ruido de formato, no una fila de datos que haya que reportar como inválida.
            if (code is null && location is null && lot is null && expiration is null && quantity is null && uom is null)
            {
                continue;
            }

            rows.Add(new InitialBalanceSheetRow(rowNumber, code, location, lot, expiration, quantity, uom));
        }

        return new InitialBalanceSheet(true, rows);
    }

    private static Dictionary<string, int> MapHeaderColumns(IXLWorksheet worksheet)
    {
        var header = worksheet.Row(1);
        var lastColumn = header.LastCellUsed()?.Address.ColumnNumber ?? 0;
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var column = 1; column <= lastColumn; column++)
        {
            var name = header.Cell(column).GetString().Trim();
            if (!string.IsNullOrEmpty(name) && !columns.ContainsKey(name))
            {
                columns[name] = column;
            }
        }

        return columns;
    }

    // Un número fuera del rango de decimal (1e30) no tumba el archivo entero: llega como texto con exponente, que la regla de la fila
    // rechaza como cantidad inválida.
    private static string NumberText(double value) =>
        Math.Abs(value) < 7.9e27
            ? Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

    private static string? Text(IXLRow row, int column)
    {
        var cell = row.Cell(column);
        var text = cell.DataType switch
        {
            XLDataType.Number => NumberText(cell.GetDouble()),
            XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => cell.GetString().Trim(),
        };

        return text.Length == 0 ? null : text;
    }
}
