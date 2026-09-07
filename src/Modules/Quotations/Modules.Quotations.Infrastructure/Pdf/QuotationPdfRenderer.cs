using System.Globalization;
using Modules.Quotations.Application;

namespace Modules.Quotations.Infrastructure.Pdf;

/// <summary>
/// Dibuja el PDF de la cotización.
///
/// Es el trazado que hasta ahora corría en el navegador (<c>quote-pdf.ts</c>, jsPDF), traído tal
/// cual: mismas coordenadas, mismos tamaños, mismos textos y el mismo orden de bloques. Se mudó
/// entero al backend para que el documento que se le manda al cliente y el que se descarga sean
/// el mismo archivo, generado en un solo lugar, sin depender de la versión del frontend que
/// estuviera abierta.
/// </summary>
internal sealed class QuotationPdfRenderer : IQuotationPdfRenderer
{
    private const double MarginX = 14;
    private const double PageBottom = 280;
    private const double RightEdge = 196;

    /// <summary>Ancho útil de cada columna del bloque facturación/entrega: la segunda arranca a
    /// 95 del margen y el documento termina en <see cref="RightEdge"/>, así que 85 deja aire
    /// entre las dos.</summary>
    private const double PartyColumnWidth = 85;

    public byte[] Render(QuotationPdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var canvas = new QuotationPdfCanvas();
        var y = 18.0;

        canvas.SetFontSize(16);
        canvas.Text($"Cotización {document.QuotationNumber}", MarginX, y);
        y += 8;

        canvas.SetFontSize(10);
        canvas.Text(
            $"Fecha: {document.CreatedAt.UtcDateTime:yyyy-MM-dd}", MarginX, y);
        if (document.ValidUntil is { } validUntil)
        {
            canvas.Text(
                $"Vigencia: {validUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
                MarginX + 70,
                y);
        }

        canvas.Text($"Asesora: {document.AdvisorLabel}", MarginX + 130, y);
        y += 10;

        canvas.SetFontSize(12);
        canvas.Text("Cliente", MarginX, y);
        y += 6;
        canvas.SetFontSize(10);
        canvas.Text($"{document.CustomerName} — CUC {document.CustomerCuc}", MarginX, y);
        y += 5;
        foreach (var line in new[] { document.CustomerContact, document.CustomerLocation })
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            canvas.Text(line, MarginX, y);
            y += 5;
        }

        y += 5;

        // Facturación y entrega, una al lado de la otra: son dos destinos distintos del mismo
        // documento y quien lo lee los compara de un vistazo. Las dos columnas avanzan juntas y
        // el bloque termina en la más alta, así ninguna se pisa con lo que sigue.
        const double columnGap = 95;
        var partyTop = y;
        var billingBottom = DrawParty(canvas, document.Billing, "Facturación", MarginX, y);
        var shippingBottom = DrawParty(
            canvas, document.Shipping, "Entrega", MarginX + columnGap, partyTop);
        y = Math.Max(billingBottom, shippingBottom) + 5;

        canvas.SetFontSize(12);
        canvas.Text("Detalle", MarginX, y);
        y += 6;

        var columns = new[]
        {
            ("Producto", MarginX),
            ("Cant.", MarginX + 90),
            ("Precio unit.", MarginX + 110),
            ("Desc. %", MarginX + 145),
            ("Subtotal", MarginX + 165),
        };

        canvas.SetFontSize(9);
        canvas.SetBold(true);
        foreach (var (label, x) in columns)
        {
            canvas.Text(label, x, y);
        }

        canvas.SetBold(false);
        y += 2;
        canvas.Line(MarginX, y, RightEdge, y);
        y += 5;

        foreach (var item in document.Items)
        {
            if (y > PageBottom)
            {
                canvas.AddPage();
                y = 18;
            }

            canvas.Text(Truncate(item.ProductName, 48), columns[0].Item2, y);
            canvas.Text(FormatQuantity(item.Quantity), columns[1].Item2, y);
            canvas.Text(
                QuotationPdfCanvas.FormatCurrency(item.UnitPrice, document.Currency),
                columns[2].Item2,
                y);
            canvas.Text($"{FormatQuantity(item.DiscountPercentage)}%", columns[3].Item2, y);
            canvas.Text(
                QuotationPdfCanvas.FormatCurrency(item.Subtotal, document.Currency),
                columns[4].Item2,
                y);
            y += 6;
        }

        y += 2;
        canvas.Line(MarginX, y, RightEdge, y);
        y += 7;

        canvas.SetFontSize(10);
        canvas.Text(
            $"Subtotal: {QuotationPdfCanvas.FormatCurrency(document.Subtotal, document.Currency)}",
            MarginX + 120,
            y);
        y += 5;
        canvas.Text(
            $"Descuento: {QuotationPdfCanvas.FormatCurrency(document.DiscountAmount, document.Currency)}",
            MarginX + 120,
            y);
        y += 5;
        canvas.Text(
            $"IVA incluido ({FormatQuantity(document.TaxPercentage)}%): " +
            QuotationPdfCanvas.FormatCurrency(document.TaxAmount, document.Currency),
            MarginX + 120,
            y);
        y += 6;
        canvas.SetFontSize(12);
        canvas.SetBold(true);
        canvas.Text(
            $"Total: {QuotationPdfCanvas.FormatCurrency(document.Total, document.Currency)}",
            MarginX + 120,
            y);
        canvas.SetBold(false);
        y += 10;

        canvas.SetFontSize(10);
        if (!string.IsNullOrWhiteSpace(document.PaymentMethod))
        {
            canvas.Text($"Forma de pago: {document.PaymentMethod}", MarginX, y);
            y += 6;
        }

        // A dónde paga quien recibe el documento. Va junto a la forma de pago y no en el
        // encabezado: es una instrucción de cobro, se lee después del total.
        if (document.BillingAccount is { } account)
        {
            canvas.SetBold(true);
            canvas.Text("Consignar a", MarginX, y);
            canvas.SetBold(false);
            y += 5;
            canvas.Text(
                JoinLine([
                    account.CompanyName,
                    string.IsNullOrWhiteSpace(account.CompanyTaxId)
                        ? null
                        : $"NIT {account.CompanyTaxId}",
                ]),
                MarginX,
                y);
            y += 5;
            canvas.Text(
                $"{account.BankName} · {account.AccountNumber} · {account.Currency}",
                MarginX,
                y);
            y += 6;
        }

        if (!string.IsNullOrWhiteSpace(document.Notes))
        {
            canvas.Text($"Observaciones: {document.Notes}", MarginX, y);
        }

        return canvas.ToArray();
    }

    /// <summary>
    /// Dibuja una parte en su columna y devuelve la altura donde terminó. Una parte sin datos
    /// propios se resuelve en una línea: repetir el domicilio del cliente tres veces no informa
    /// nada y empuja el detalle de productos a la página siguiente.
    /// </summary>
    private static double DrawParty(
        QuotationPdfCanvas canvas,
        QuotationPdfParty party,
        string title,
        double x,
        double top)
    {
        var y = top;
        canvas.SetBold(true);
        canvas.Text(title, x, y);
        canvas.SetBold(false);
        y += 5;

        if (party.SameAsCustomer)
        {
            canvas.Text("Los mismos datos del cliente", x, y);
            return y;
        }

        foreach (var line in new[] { party.Name, party.Contact, party.Location })
        {
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            foreach (var wrapped in canvas.SplitTextToSize(line, PartyColumnWidth))
            {
                canvas.Text(wrapped, x, y);
                y += 5;
            }
        }

        return y - 5;
    }

    /// <summary>
    /// Cantidades y porcentajes salen sin ceros de más: el navegador imprimía el número de
    /// JavaScript, que no arrastra la escala decimal que trae la columna de la base.
    /// </summary>
    private static string FormatQuantity(decimal value) =>
        value.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>Une lo que hay y descarta lo vacío: un dato faltante no deja un separador
    /// colgando.</summary>
    private static string JoinLine(IEnumerable<string?> parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
