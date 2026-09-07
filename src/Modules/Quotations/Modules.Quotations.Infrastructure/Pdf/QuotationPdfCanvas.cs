using System.Globalization;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Modules.Quotations.Infrastructure.Pdf;

/// <summary>
/// Una hoja A4 que se dibuja con las mismas coordenadas que usaba el generador anterior: la
/// posición en milímetros y el tamaño de letra en puntos.
///
/// Existe para que <see cref="QuotationPdfRenderer"/> se pueda leer al lado del archivo que
/// reemplaza (<c>quote-pdf.ts</c>) y se vea que dibuja lo mismo, en el mismo lugar. PDFsharp
/// mezcla las dos unidades —si el <c>XGraphics</c> está en milímetros, el cuerpo de la letra
/// también se interpreta en milímetros, y un "10" pensado en puntos sale casi tres veces más
/// grande— así que la conversión se hace acá, una vez, en vez de repartirla por el trazado.
/// </summary>
internal sealed class QuotationPdfCanvas : IDisposable
{
    private const double PointsPerMillimeter = 72.0 / 25.4;

    private readonly PdfDocument document = new();
    private XGraphics graphics;
    private double fontSize = 10;
    private bool isBold;

    internal QuotationPdfCanvas()
    {
        QuotationPdfFonts.EnsureRegistered();
        graphics = StartPage();
    }

    /// <summary>El texto en negrita hasta que se diga lo contrario.</summary>
    internal void SetBold(bool bold) => isBold = bold;

    internal void SetFontSize(double points) => fontSize = points;

    /// <summary>Escribe con la línea base en <paramref name="y"/>, como hacía jsPDF.</summary>
    internal void Text(string text, double x, double y)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        graphics.DrawString(
            text,
            CurrentFont(),
            XBrushes.Black,
            new XPoint(ToPoints(x), ToPoints(y)),
            XStringFormats.BaseLineLeft);
    }

    internal void Line(double fromX, double fromY, double toX, double toY) =>
        graphics.DrawLine(
            new XPen(XColors.Black, 0.2 * PointsPerMillimeter),
            ToPoints(fromX),
            ToPoints(fromY),
            ToPoints(toX),
            ToPoints(toY));

    internal void AddPage()
    {
        graphics.Dispose();
        graphics = StartPage();
    }

    /// <summary>
    /// Parte el texto en renglones que entren en <paramref name="widthMm"/>, midiendo el ancho
    /// real de cada palabra — no contando caracteres. Una dirección larga se acomoda en dos
    /// renglones en vez de cortarse a la mitad o meterse en la columna de al lado.
    /// </summary>
    internal IReadOnlyList<string> SplitTextToSize(string text, double widthMm)
    {
        var maxWidth = ToPoints(widthMm);
        var font = CurrentFont();
        var lines = new List<string>();
        var current = string.Empty;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length > 0 && graphics.MeasureString(candidate, font).Width > maxWidth)
            {
                lines.Add(current);
                current = word;
                continue;
            }

            current = candidate;
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        // Un texto que no entra ni con una sola palabra igual tiene que salir: se imprime como
        // está y se pasa un poco, que es lo que hacía el generador anterior.
        return lines.Count > 0 ? lines : [text];
    }

    internal byte[] ToArray()
    {
        graphics.Dispose();

        using var buffer = new MemoryStream();
        document.Save(buffer, closeStream: false);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        graphics.Dispose();
        document.Dispose();
    }

    private XGraphics StartPage()
    {
        var page = document.AddPage();
        page.Size = PageSize.A4;
        page.Orientation = PageOrientation.Portrait;
        return XGraphics.FromPdfPage(page, XGraphicsUnit.Point);
    }

    private XFont CurrentFont() => new(
        QuotationPdfFonts.FamilyName,
        fontSize,
        isBold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    private static double ToPoints(double millimeters) =>
        millimeters * PointsPerMillimeter;

    /// <summary>
    /// Los importes tal como los imprimía el navegador: <c>es-CO</c>, punto para los miles y
    /// coma para los decimales, sin centavos en pesos y con centavos en dólares. Se arma el
    /// formato en vez de pedirle "C" a la cultura para que no dependa de la versión de ICU que
    /// tenga la máquina — el mismo número tiene que salir igual acá y en el contenedor.
    /// </summary>
    internal static string FormatCurrency(decimal value, string currency)
    {
        var format = new NumberFormatInfo
        {
            NumberGroupSeparator = ".",
            NumberDecimalSeparator = ",",
        };

        return string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? $"US$ {value.ToString("#,##0.00", format)}"
            : $"$ {value.ToString("#,##0", format)}";
    }
}
