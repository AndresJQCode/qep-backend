using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

// Las secciones configurables del PDF (spec 2026-11-16 §4.1 y §4.3).
public sealed class PdfContentTests
{
    [Fact]
    public void CreateRejectsAnUnknownVariable()
    {
        var terms = PdfSection.Empty(PdfSectionKey.Terms) with { Title = "{{ciudad}}" };

        var error = Assert.Throws<QuotationsDomainException>(() => PdfContent.Create(
            PdfSection.Empty(PdfSectionKey.PaymentNotice), terms, PdfSection.Empty(PdfSectionKey.DeliveryProcess),
            PdfPaymentAccount.Empty));

        Assert.Equal("quotation.pdf_content.unknown_variable", error.Code);
        // La consola marca el input con la misma clave del validador.
        var field = Assert.Single(error.FieldErrors);
        Assert.Equal("Terms.Title", field.Key);
        Assert.Contains("{{ciudad}}", Assert.Single(field.Value), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateRejectsAnUnknownVariableInTheHtml()
    {
        var delivery = PdfSection.Empty(PdfSectionKey.DeliveryProcess) with { ContentHtml = "<p>{{Empresa}}</p>" };

        var error = Assert.Throws<QuotationsDomainException>(() => PdfContent.Create(
            PdfSection.Empty(PdfSectionKey.PaymentNotice), PdfSection.Empty(PdfSectionKey.Terms), delivery, PdfPaymentAccount.Empty));

        Assert.Equal("quotation.pdf_content.unknown_variable", error.Code);
        Assert.Equal("DeliveryProcess.ContentHtml", Assert.Single(error.FieldErrors).Key);
    }

    [Fact]
    public void CreateAcceptsKnownVariables()
    {
        var terms = PdfSection.Empty(PdfSectionKey.Terms) with
        {
            Enabled = true,
            Title = "Términos de {{empresa}}",
            ContentHtml = "<p>Cotización {{ numero }} para {{cliente}}.</p>",
        };

        var content = PdfContent.Create(
            PdfSection.Empty(PdfSectionKey.PaymentNotice), terms, PdfSection.Empty(PdfSectionKey.DeliveryProcess),
            PdfPaymentAccount.Empty);

        Assert.Equal(terms, content.Terms);
        Assert.Equal(
            [PdfSectionKey.PaymentNotice, PdfSectionKey.Terms, PdfSectionKey.DeliveryProcess],
            content.All.Select(section => section.Key));
    }

    [Fact]
    public void EmptyIsThreeDisabledSectionsForBothDocuments()
    {
        Assert.All(PdfContent.Empty.All, section =>
        {
            Assert.False(section.Enabled);
            Assert.True(section.InQuotation);
            Assert.True(section.InOrder);
            Assert.Equal(string.Empty, section.Title);
            Assert.Equal(string.Empty, section.ContentHtml);
        });
        Assert.Equal(
            [PdfSectionKey.PaymentNotice, PdfSectionKey.Terms, PdfSectionKey.DeliveryProcess],
            PdfContent.Empty.All.Select(section => section.Key));
    }

    [Fact]
    public void PrintsOnFollowsTheFlags()
    {
        var disabled = PdfSection.Empty(PdfSectionKey.Terms);
        Assert.False(disabled.PrintsOn(isOrder: false));
        Assert.False(disabled.PrintsOn(isOrder: true));

        var quotationOnly = disabled with { Enabled = true, InOrder = false };
        Assert.True(quotationOnly.PrintsOn(isOrder: false));
        Assert.False(quotationOnly.PrintsOn(isOrder: true));
    }

    // La cuenta para el pago (spec 2026-11-16 pdf-payment-account): misma regla que una sección.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void PaymentAccountPrintsOnlyWhenEnabledAndTheDocumentFlagIsOn(bool enabled, bool inQuotation, bool inOrder)
    {
        var account = new PdfPaymentAccount(enabled, inQuotation, inOrder);

        Assert.Equal(enabled && inQuotation, account.PrintsOn(isOrder: false));
        Assert.Equal(enabled && inOrder, account.PrintsOn(isOrder: true));
    }

    // Apagada por defecto (decisión 1 del spec): un tenant sin fila no imprime la cuenta.
    [Fact]
    public void EmptyPaymentAccountIsOffForBothDocuments()
    {
        Assert.False(PdfContent.Empty.PaymentAccount.Enabled);
        Assert.True(PdfContent.Empty.PaymentAccount.InQuotation);
        Assert.True(PdfContent.Empty.PaymentAccount.InOrder);
        Assert.Equal(PdfPaymentAccount.Empty, PdfContent.Empty.PaymentAccount);
    }
}
