using System.Text;
using Modules.Quotations.Application;
using Modules.Quotations.Infrastructure.Pdf;
using PdfSharp.Pdf.IO;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// El PDF se arma en el backend (antes lo armaba el navegador con jsPDF). Estas pruebas leen el
/// documento de vuelta y buscan el texto adentro: renderizar sin explotar no prueba nada — lo que
/// importa es que lo que se cotizó esté impreso.
/// </summary>
public sealed class QuotationPdfRendererTests
{
    private static readonly QuotationPdfRenderer Renderer = new();

    private static QuotationPdfDocument NewDocument(
        IReadOnlyList<QuotationPdfLine>? items = null,
        string currency = "COP",
        QuotationPdfBillingAccount? billingAccount = null,
        QuotationPdfParty? shipping = null) =>
        new(
            "QUO-2026-0001",
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
            new DateOnly(2026, 9, 8),
            "Yakelin Ojeda",
            "CUC-0042",
            "3001234567 · yakelin@example.com",
            "Calle 10 # 5-20, Medellín",
            new QuotationPdfParty(true, "Yakelin Ojeda", string.Empty, string.Empty),
            shipping ?? new QuotationPdfParty(true, "Yakelin Ojeda", string.Empty, string.Empty),
            "asesora@example.com",
            currency,
            billingAccount,
            "Transferencia bancaria",
            null,
            items ?? [new QuotationPdfLine("Aceite esencial de lavanda", 3, 119_000m, 5m, 339_150m)],
            285_000m,
            17_850m,
            19m,
            54_150m,
            339_150m);

    /// <summary>Todo el texto del documento, página por página, tal como quedó en el archivo.</summary>
    private static string TextOf(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

        var text = new StringBuilder();
        for (var index = 0; index < document.PageCount; index++)
        {
            foreach (var content in document.Pages[index].Contents)
            {
                text.Append(Encoding.UTF8.GetString(content.Stream.UnfilteredValue));
            }
        }

        return text.ToString();
    }

    [Fact]
    public void RenderProducesAPdfFile()
    {
        var pdf = Renderer.Render(NewDocument());

        Assert.Equal("%PDF-"u8.ToArray(), pdf.Take(5).ToArray());
    }

    [Fact]
    public void RenderPrintsTheHeaderTheCustomerAndTheLine()
    {
        var text = TextOf(Renderer.Render(NewDocument()));

        Assert.Contains("QUO-2026-0001", text, StringComparison.Ordinal);
        Assert.Contains("2026-08-24", text, StringComparison.Ordinal);
        Assert.Contains("2026-09-08", text, StringComparison.Ordinal);
        Assert.Contains("Yakelin Ojeda", text, StringComparison.Ordinal);
        Assert.Contains("CUC-0042", text, StringComparison.Ordinal);
        Assert.Contains("asesora@example.com", text, StringComparison.Ordinal);
        Assert.Contains("Aceite esencial de lavanda", text, StringComparison.Ordinal);
        Assert.Contains("Transferencia bancaria", text, StringComparison.Ordinal);
    }

    // Los importes se imprimen como los imprimía el navegador: punto para los miles, sin
    // centavos en pesos.
    [Fact]
    public void RenderPrintsCopAmountsWithoutDecimals()
    {
        var text = TextOf(Renderer.Render(NewDocument()));

        Assert.Contains("339.150", text, StringComparison.Ordinal);
        Assert.DoesNotContain("339.150,00", text, StringComparison.Ordinal);
    }

    // En dólares sí van los centavos: redondear USD 12,50 a "13" mentiría sobre el precio.
    [Fact]
    public void RenderPrintsUsdAmountsWithCents()
    {
        var document = NewDocument(
            items: [new QuotationPdfLine("Aceite esencial", 1, 12.5m, 0m, 12.5m)],
            currency: "USD");

        var text = TextOf(Renderer.Render(document));

        Assert.Contains("US$ 12,50", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderPrintsWhereToPayWhenTheQuotationChoseAnAccount()
    {
        var document = NewDocument(
            billingAccount: new QuotationPdfBillingAccount(
                "Qcode SAS", "900123456", "Bancolombia", "12345678", "COP"));

        var text = TextOf(Renderer.Render(document));

        Assert.Contains("Consignar a", text, StringComparison.Ordinal);
        Assert.Contains("Qcode SAS", text, StringComparison.Ordinal);
        Assert.Contains("NIT 900123456", text, StringComparison.Ordinal);
        Assert.Contains("Bancolombia", text, StringComparison.Ordinal);
    }

    // Una parte con datos propios se imprime; una sin datos dice que son los del cliente, en vez
    // de repetir el domicilio por tercera vez.
    [Fact]
    public void RenderPrintsAShippingPartyThatHasItsOwnData()
    {
        var document = NewDocument(
            shipping: new QuotationPdfParty(
                false, "Bodega norte", "3009999999", "Carrera 50 # 100-10, Bogotá"));

        var text = TextOf(Renderer.Render(document));

        Assert.Contains("Bodega norte", text, StringComparison.Ordinal);
        Assert.Contains("Carrera 50", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSaysWhenAPartyHasNoDataOfItsOwn()
    {
        var text = TextOf(Renderer.Render(NewDocument()));

        Assert.Contains("Los mismos datos del cliente", text, StringComparison.Ordinal);
    }

    // El detalle largo sigue en una segunda página en vez de escribirse sobre el pie.
    [Fact]
    public void RenderBreaksALongDetailIntoASecondPage()
    {
        var items = Enumerable.Range(1, 60)
            .Select(number => new QuotationPdfLine($"Producto {number}", 1, 1_000m, 0m, 1_000m))
            .ToArray();

        using var stream = new MemoryStream(Renderer.Render(NewDocument(items)));
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

        Assert.Equal(2, document.PageCount);
    }
}
