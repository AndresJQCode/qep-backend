using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BuildingBlocks.Application.RichText;
using BuildingBlocks.Domain.Localization;
using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>Una sección configurable tal como la imprime el documento: el título y los bloques con las variables ya resueltas
/// (spec 2026-11-16 §5.3). La plantilla pinta cada texto como cadena, nunca como markup.</summary>
public sealed record QuotationPdfSection(string Title, IReadOnlyList<RichTextBlock> Blocks);

/// <summary>Las tres secciones del documento. Una es null si no sale en este tipo de documento; viajan como
/// <c>data.sections.paymentNotice</c>, <c>.terms</c> y <c>.deliveryProcess</c>. <c>ShowPaymentAccount</c>
/// (<c>data.sections.showPaymentAccount</c>) dice si el documento imprime las líneas de la cuenta para el pago
/// (spec 2026-11-16 pdf-payment-account).</summary>
public sealed record QuotationPdfSections(
    QuotationPdfSection? PaymentNotice,
    QuotationPdfSection? Terms,
    QuotationPdfSection? DeliveryProcess,
    bool ShowPaymentAccount);

/// <summary>
/// Arma las secciones del documento desde el contenido del tenant (spec 2026-11-16 §4.1, §4.3, §5.3) y calcula la huella con que se
/// vence el PDF guardado de la cotización (§5.4). Las dos cosas deciden con <see cref="PdfSection.PrintsOn"/>: si decidieran por
/// caminos distintos, el caché podría quedarse con un documento que ya no dice lo configurado.
/// </summary>
public static class QuotationPdfSectionsBuilder
{
    // Los mismos nombres que `meses` de `quotation.typ`: la variable tiene que decir la fecha igual que la ficha del documento.
    private static readonly string[] Months =
    [
        "enero", "febrero", "marzo", "abril", "mayo", "junio",
        "julio", "agosto", "septiembre", "octubre", "noviembre", "diciembre",
    ];

    public static QuotationPdfSections Build(PdfContent content, QuotationPdfDocument document, IRichTextReader reader)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reader);

        var isOrder = document.Kind == QuotationPdfKind.Order;
        var values = ValuesOf(document);

        // El aviso sale por sus propias banderas, haya o no cuenta de cobro (spec 2026-11-16 pdf-payment-account, decisión 4).
        return new QuotationPdfSections(
            SectionOf(content.PaymentNotice, isOrder, values, reader),
            SectionOf(content.Terms, isOrder, values, reader),
            SectionOf(content.DeliveryProcess, isOrder, values, reader),
            content.PaymentAccount.PrintsOn(isOrder));
    }

    /// <summary>Los valores de las variables, formateados como los imprime la plantilla (spec 2026-11-16 §4.3). La empresa y su NIT
    /// salen null sin cuenta de cobro, y la vigencia, en un pedido o sin vencimiento: <see cref="QuotationPdfVariables.Fill"/> los deja
    /// vacíos.</summary>
    public static QuotationPdfVariableValues ValuesOf(QuotationPdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var validUntil = document.Kind == QuotationPdfKind.Order || document.ValidUntil is not { } date ? null : DateText(date);
        return new QuotationPdfVariableValues(
            document.BillingAccount?.CompanyName,
            document.BillingAccount?.CompanyTaxId,
            document.CustomerName,
            document.QuotationNumber,
            DateText(document.IssuedOn),
            validUntil,
            document.AdvisorLabel,
            document.Symbol + " " + AmountText(document.Total, document));
    }

    /// <summary>
    /// SHA-256 en hex minúscula de <c>Key|Title|ContentHtml</c> de cada sección que sale en ese tipo de documento, unidas con
    /// <c>\n</c> en el orden de <see cref="PdfContent.All"/>; sin secciones, el hash de la cadena vacía. Va el texto guardado, con
    /// los tokens: los valores de las variables salen de la cotización y ya los cubre su versión. Apagar una sección o quitarle el
    /// tipo de documento la saca de la lista, así que también cambia la huella. La cuenta para el pago queda fuera a propósito
    /// (owner 2026-11-16: el despliegue no regenera ningún PDF); su cambio vence el caché al guardar
    /// (<c>InvalidateQuotationPdfsAsync</c>).
    /// </summary>
    public static string Fingerprint(PdfContent content, QuotationPdfKind kind, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        var isOrder = kind == QuotationPdfKind.Order;
        var text = string.Join(
            '\n',
            content.All
                .Where(section => section.PrintsOn(isOrder))
                .Select(section => $"{section.Key}|{section.Title}|{section.ContentHtml}"));
        // El idioma del tenant cambia lo que el PDF imprime, así que entra a la huella (spec 2026-11-22, i18n). Sólo cuando no es
        // el por defecto: así la huella de los PDF ya guardados no cambia y el despliegue no regenera ninguno.
        var resolved = SupportedLanguages.OrDefault(language);
        if (resolved != SupportedLanguages.Default)
        {
            text += $"\nlanguage|{resolved}";
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static QuotationPdfSection? SectionOf(
        PdfSection section, bool isOrder, QuotationPdfVariableValues values, IRichTextReader reader) =>
        section.PrintsOn(isOrder)
            ? new QuotationPdfSection(
                QuotationPdfVariables.Fill(section.Title, values),
                RichTextTree.MapText(reader.Read(section.ContentHtml), text => QuotationPdfVariables.Fill(text, values)))
            : null;

    // Como `fecha()` de la plantilla: «16 de noviembre de 2026», día sin cero y mes en minúscula.
    private static string DateText(DateOnly date) =>
        $"{date.Day.ToString(CultureInfo.InvariantCulture)} de {Months[date.Month - 1]} de {date.Year.ToString(CultureInfo.InvariantCulture)}";

    // Como `importe()` de la plantilla: sin decimales redondea al entero; con decimales imprime siempre dos, sea cual sea
    // `Decimals`. Typst recibe el monto como número JSON y redondea en f64 la mitad lejos del cero, así que el cálculo va en
    // `double` con `AwayFromZero`: en `decimal`, 1,005 daría 1,01 y la fila del total del mismo PDF dice 1,00.
    private static string AmountText(decimal value, QuotationPdfDocument document)
    {
        var amount = (double)value;
        if (document.Decimals == 0)
        {
            return Thousands((decimal)Math.Round(amount, MidpointRounding.AwayFromZero), document.ThousandsSeparator);
        }

        // Un total negativo no existe; si llegara, sale con el signo adelante y no con el redondeo hacia abajo de `calc.quo`.
        var cents = Math.Abs((decimal)Math.Round(amount * 100d, MidpointRounding.AwayFromZero));
        var sign = value < 0 && cents > 0 ? "-" : string.Empty;
        return sign + Thousands(decimal.Truncate(cents / 100m), document.ThousandsSeparator) + document.DecimalSeparator
            + (cents % 100m).ToString("00", CultureInfo.InvariantCulture);
    }

    // `miles()` de la plantilla: grupos de tres desde la derecha, con el separador del tenant.
    private static string Thousands(decimal integer, string separator)
    {
        var digits = Math.Abs(integer).ToString("0", CultureInfo.InvariantCulture);
        var groups = new List<string>();
        while (digits.Length > 3)
        {
            groups.Insert(0, digits[^3..]);
            digits = digits[..^3];
        }

        groups.Insert(0, digits);
        return (integer < 0 ? "-" : string.Empty) + string.Join(separator, groups);
    }
}
