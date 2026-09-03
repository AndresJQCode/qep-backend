using ClosedXML.Excel;
using Modules.Catalog.Application;

namespace Modules.Catalog.Infrastructure.Excel;

/// <summary>
/// Arma el Excel del catalogo con ClosedXML.
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
/// </summary>
internal sealed class ClosedXmlProductExportBuilder : IProductExportWorkbookBuilder
{
    private const string SheetName = "Productos";

    // `AdjustToContents()` ajusta al ancho exacto del texto, sin margen. Mismo piso que la
    // plantilla de importacion de clientes, por el mismo motivo: la cabecera queda pegada al
    // borde de la celda siguiente y es incomoda de leer.
    private const double MinimumColumnWidth = 14;

    // Formato de moneda de la celda de precio. Se guarda como numero con formato, no como texto
    // ya formateado: un texto no se puede sumar ni ordenar en la planilla.
    private const string CopNumberFormat = "#,##0";

    private static readonly string[] FixedHeaders =
    [
        "Codigo",
        "Nombre",
        "Descripcion",
        "Estado",
        "Tasa de impuesto",
        "Precio base USD",
        "Precio base COP",
    ];

    public byte[] Build(IReadOnlyList<ProductExportRow> products)
    {
        // Las columnas de escala salen del catalogo entero, no de cada producto: es lo que hace
        // que una escala compartida ocupe una sola columna.
        var scaleColumns = products
            .SelectMany(product => product.Scales)
            .Select(scale => (scale.FromUnit, scale.ToUnit))
            .Distinct()
            .OrderBy(scale => scale.FromUnit)
            .ThenBy(scale => scale.ToUnit)
            .ToList();

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName);

        for (var index = 0; index < FixedHeaders.Length; index++)
        {
            sheet.Cell(1, index + 1).Value = FixedHeaders[index];
        }

        for (var index = 0; index < scaleColumns.Count; index++)
        {
            sheet.Cell(1, FixedHeaders.Length + index + 1).Value =
                HeaderFor(scaleColumns[index].FromUnit, scaleColumns[index].ToUnit);
        }

        sheet.Row(1).Style.Font.Bold = true;

        for (var rowIndex = 0; rowIndex < products.Count; rowIndex++)
        {
            var product = products[rowIndex];
            var row = rowIndex + 2;

            sheet.Cell(row, 1).Value = product.Code;
            sheet.Cell(row, 2).Value = product.Name;
            sheet.Cell(row, 3).Value = product.Description ?? string.Empty;
            sheet.Cell(row, 4).Value = product.IsActive ? "Activo" : "Inactivo";
            sheet.Cell(row, 5).Value = product.TaxRateName ?? string.Empty;
            SetMoney(sheet.Cell(row, 6), product.PriceBaseUsd);
            SetMoney(sheet.Cell(row, 7), product.PriceBaseCop);

            // Indexado por rango: buscar la escala del producto para cada columna es lo que deja
            // la celda vacia cuando ese producto no tiene ese tramo.
            var byRange = product.Scales.ToDictionary(
                scale => (scale.FromUnit, scale.ToUnit),
                scale => scale.PriceCop);

            for (var index = 0; index < scaleColumns.Count; index++)
            {
                if (byRange.TryGetValue(scaleColumns[index], out var priceCop))
                {
                    SetMoney(sheet.Cell(row, FixedHeaders.Length + index + 1), priceCop);
                }
            }
        }

        sheet.Columns().AdjustToContents();
        foreach (var column in sheet.Columns())
        {
            if (column.Width < MinimumColumnWidth) column.Width = MinimumColumnWidth;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>El encabezado de una escala: el rango que la identifica.</summary>
    private static string HeaderFor(int fromUnit, int toUnit) => $"{fromUnit}-{toUnit}";

    /// <summary>
    /// Deja la celda intacta cuando no hay precio. Escribir 0 seria peor que no escribir nada:
    /// se lee como un producto que sale gratis en ese tramo.
    /// </summary>
    private static void SetMoney(IXLCell cell, decimal? value)
    {
        if (value is null) return;
        cell.Value = value.Value;
        cell.Style.NumberFormat.Format = CopNumberFormat;
    }
}
