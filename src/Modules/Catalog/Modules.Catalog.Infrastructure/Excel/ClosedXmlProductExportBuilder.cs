using ClosedXML.Excel;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;
using Modules.Tenancy.Application;

namespace Modules.Catalog.Infrastructure.Excel;

/// <summary>
/// Arma el Excel del catalogo con ClosedXML.
///
/// Una fila por **variante** (spec 2026-10-20 §7): Codigo y Codigo de barras son de la variante;
/// Producto, Descripcion y Tasa de impuesto repiten los del producto en cada una de sus filas, y
/// Variante lleva el nombre visible (vacio en la variante por defecto, que no se distingue por
/// opciones). Los precios y las escalas tambien son de la variante.
///
/// Lo particular del formato son las escalas: en vez de una fila por escala (que repetiria el
/// producto y obligaria a leer la planilla agrupando a mano), cada escala distinta del catalogo
/// es **una columna**. "Distinta" es el par desde-hasta: dos productos que comparten el tramo
/// 1-9 comparten la columna, y un producto que no tiene ese tramo deja la celda vacia. Asi la
/// planilla se lee como una matriz de precios y las columnas se pueden comparar entre productos.
///
/// El orden de las columnas es por unidad de inicio y despues por la de fin, no el de aparicion:
/// una planilla donde 10-19 cae antes que 1-9 porque asi vinieron los productos es dificil de
/// leer y cambia entre exportaciones del mismo catalogo.
///
/// Spec 2026-10-08: una columna de precio base por moneda en uso y, por escala, su descuento
/// seguido del final derivado en cada una de esas monedas. Los finales salen de
/// <c>PriceScale.FinalFor</c>: el mismo redondeo que la respuesta de la API.
/// </summary>
internal sealed class ClosedXmlProductExportBuilder : IProductExportWorkbookBuilder
{
    private const string SheetName = "Productos";

    // `AdjustToContents()` ajusta al ancho exacto del texto, sin margen. Mismo piso que la
    // plantilla de importacion de clientes, por el mismo motivo: la cabecera queda pegada al
    // borde de la celda siguiente y es incomoda de leer.
    private const double MinimumColumnWidth = 14;

    private const string DiscountNumberFormat = "0.##";

    private static readonly string[] FixedHeaders =
    [
        "Código",
        "Código de barras",
        "Producto",
        "Variante",
        "Descripción",
        "Estado",
        "Tasa de impuesto",
    ];

    public byte[] Build(IReadOnlyList<ProductExportRow> rows, IReadOnlyList<CurrencyInfo> currencies)
    {
        // Las columnas de escala salen del catalogo entero, no de cada variante: es lo que hace
        // que una escala compartida ocupe una sola columna.
        var scaleColumns = rows
            .SelectMany(row => row.Scales)
            .Select(scale => (scale.FromUnit, scale.ToUnit))
            .Distinct()
            .OrderBy(scale => scale.FromUnit)
            .ThenBy(scale => scale.ToUnit)
            .ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName);

        var headers = new List<string>(FixedHeaders);
        headers.AddRange(currencies.Select(currency => $"Precio base {currency.Code}"));
        foreach (var (fromUnit, toUnit) in scaleColumns)
        {
            headers.Add($"Descuento {fromUnit}-{toUnit} (%)");
            headers.AddRange(currencies.Select(currency => $"Final {fromUnit}-{toUnit} {currency.Code}"));
        }

        for (var index = 0; index < headers.Count; index++)
        {
            sheet.Cell(1, index + 1).Value = headers[index];
        }

        sheet.Row(1).Style.Font.Bold = true;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var variant = rows[rowIndex];
            var row = rowIndex + 2;

            sheet.Cell(row, 1).Value = variant.Code;
            sheet.Cell(row, 2).Value = variant.Barcode ?? string.Empty;
            sheet.Cell(row, 3).Value = variant.ProductName;
            sheet.Cell(row, 4).Value = variant.VariantName ?? string.Empty;
            sheet.Cell(row, 5).Value = variant.Description ?? string.Empty;
            sheet.Cell(row, 6).Value = variant.IsActive ? "Activo" : "Inactivo";
            sheet.Cell(row, 7).Value = variant.TaxRateName ?? string.Empty;

            var column = FixedHeaders.Length + 1;
            foreach (var currency in currencies)
            {
                SetMoney(sheet.Cell(row, column++), PriceOf(variant, currency), currency);
            }

            // Indexer and not ToDictionary: a repeated range must not throw from inside an
            // export; the last one wins, as in ProductPriceChangeDetector.
            var discounts = new Dictionary<(int, int), decimal>();
            foreach (var scale in variant.Scales)
            {
                discounts[(scale.FromUnit, scale.ToUnit)] = scale.DiscountPercent;
            }

            // A variant without the range leaves the discount and every final empty.
            foreach (var range in scaleColumns)
            {
                var hasScale = discounts.TryGetValue(range, out var discount);
                if (hasScale)
                {
                    sheet.Cell(row, column).Value = discount;
                    sheet.Cell(row, column).Style.NumberFormat.Format = DiscountNumberFormat;
                }

                column++;
                foreach (var currency in currencies)
                {
                    var final = hasScale ? PriceScale.FinalFor(PriceOf(variant, currency), discount) : null;
                    SetMoney(sheet.Cell(row, column++), final, currency);
                }
            }
        }

        sheet.Columns().AdjustToContents();
        foreach (var sheetColumn in sheet.Columns())
        {
            if (sheetColumn.Width < MinimumColumnWidth) sheetColumn.Width = MinimumColumnWidth;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static decimal? PriceOf(ProductExportRow row, CurrencyInfo currency) =>
        row.Prices.TryGetValue(currency.Code, out var amount) ? amount : null;

    /// <summary>
    /// Leaves the cell untouched without a value: 0 would read as "free in this tier". The format
    /// follows the currency's decimals. Stored as a number with a format, not as formatted text: a
    /// text cell cannot be summed or sorted in the spreadsheet.
    /// </summary>
    private static void SetMoney(IXLCell cell, decimal? value, CurrencyInfo currency)
    {
        if (value is null) return;
        cell.Value = value.Value;
        cell.Style.NumberFormat.Format = currency.Decimals == 0 ? "#,##0" : "#,##0.00";
    }
}
