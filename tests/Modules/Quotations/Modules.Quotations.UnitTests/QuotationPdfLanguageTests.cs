using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-11-22 (i18n): el PDF sale en el idioma del tenant. En España la cotización es «presupuesto» y el asesor,
/// «comercial» (owner, 2026-11-22). Cambiar el idioma vence el PDF guardado por la huella, sin columna nueva; y como el
/// idioma por defecto no entra a la huella, el despliegue no regenera ningún PDF de los tenants de hoy.
/// </summary>
public sealed class QuotationPdfLanguageTests
{
    [Fact]
    public void TheDefaultLanguageLeavesTheFingerprintAsItWas()
    {
        var content = PdfContent.Empty with { Terms = Section() };

        Assert.Equal(
            QuotationPdfSectionsBuilder.Fingerprint(content, QuotationPdfKind.Quotation),
            QuotationPdfSectionsBuilder.Fingerprint(content, QuotationPdfKind.Quotation, "es-419"));
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            QuotationPdfSectionsBuilder.Fingerprint(PdfContent.Empty, QuotationPdfKind.Quotation, "es-419"));
    }

    [Fact]
    public void SpainChangesTheFingerprintSoTheStoredPdfIsRegenerated()
    {
        var content = PdfContent.Empty with { Terms = Section() };

        var latinAmerica = QuotationPdfSectionsBuilder.Fingerprint(content, QuotationPdfKind.Quotation, "es-419");
        var spain = QuotationPdfSectionsBuilder.Fingerprint(content, QuotationPdfKind.Quotation, "es-ES");

        Assert.NotEqual(latinAmerica, spain);
        Assert.Matches("^[0-9a-f]{64}$", spain);
        Assert.NotEqual(spain, QuotationPdfSectionsBuilder.Fingerprint(PdfContent.Empty, QuotationPdfKind.Quotation, "es-ES"));
    }

    [Theory]
    [InlineData(QuotationPdfKind.Quotation, "es-419", "Cotizacion-COT-7.pdf")]
    [InlineData(QuotationPdfKind.Quotation, "es-ES", "Presupuesto-COT-7.pdf")]
    [InlineData(QuotationPdfKind.Order, "es-419", "Pedido-COT-7.pdf")]
    [InlineData(QuotationPdfKind.Order, "es-ES", "Pedido-COT-7.pdf")]
    public void TheFileIsNamedAfterTheDocumentInTheLanguage(QuotationPdfKind kind, string language, string expected) =>
        Assert.Equal(expected, QuotationPdfFileName.For(kind, "COT-7", language));

    private static PdfSection Section() =>
        new(PdfSectionKey.Terms, true, true, true, "Términos", "<p>Texto</p>");
}
