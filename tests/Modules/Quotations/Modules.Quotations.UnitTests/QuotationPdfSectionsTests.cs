using System.Globalization;
using BuildingBlocks.Application.RichText;
using BuildingBlocks.Infrastructure.RichText;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Tenancy.Application;

namespace Modules.Quotations.UnitTests;

// Las secciones configurables dentro del documento del PDF y la huella que vence el caché (spec 2026-11-16 §4.1, §4.3, §5.3, §5.4).
public sealed class QuotationPdfSectionsTests
{
    private static readonly IRichTextReader Reader = new RichTextHtmlReader();

    [Fact]
    public void ATenantWithoutConfigurationPrintsNoSection()
    {
        var sections = QuotationPdfSectionsBuilder.Build(PdfContent.Empty, Document(), Reader);

        Assert.Null(sections.PaymentNotice);
        Assert.Null(sections.Terms);
        Assert.Null(sections.DeliveryProcess);
    }

    [Fact]
    public void ASectionOnlyForQuotationsIsLeftOutOfTheOrder()
    {
        var content = PdfContent.Empty with { Terms = Section(PdfSectionKey.Terms, inQuotation: true, inOrder: false) };

        Assert.NotNull(QuotationPdfSectionsBuilder.Build(content, Document(), Reader).Terms);
        Assert.Null(QuotationPdfSectionsBuilder.Build(content, Order(), Reader).Terms);
    }

    [Fact]
    public void ASectionOnlyForOrdersIsLeftOutOfTheQuotation()
    {
        var content = PdfContent.Empty with
        {
            DeliveryProcess = Section(PdfSectionKey.DeliveryProcess, inQuotation: false, inOrder: true),
        };

        Assert.Null(QuotationPdfSectionsBuilder.Build(content, Document(), Reader).DeliveryProcess);
        Assert.NotNull(QuotationPdfSectionsBuilder.Build(content, Order(), Reader).DeliveryProcess);
    }

    // El aviso sale por sus propias banderas, haya o no cuenta de cobro (spec 2026-11-16 pdf-payment-account, decisión 4).
    [Fact]
    public void PaymentNoticePrintsWithoutBillingAccount()
    {
        var content = PdfContent.Empty with { PaymentNotice = Section(PdfSectionKey.PaymentNotice) };

        Assert.NotNull(QuotationPdfSectionsBuilder.Build(content, Document(), Reader).PaymentNotice);
        Assert.NotNull(QuotationPdfSectionsBuilder.Build(content, Document() with { BillingAccount = null }, Reader).PaymentNotice);
    }

    // La cuenta para el pago decide con la misma regla que una sección: encendida y marcada para ese tipo de documento.
    [Fact]
    public void ShowPaymentAccountFollowsTheFlagOfTheDocument()
    {
        var content = PdfContent.Empty with { PaymentAccount = new PdfPaymentAccount(true, InQuotation: true, InOrder: false) };

        Assert.True(QuotationPdfSectionsBuilder.Build(content, Document(), Reader).ShowPaymentAccount);
        Assert.False(QuotationPdfSectionsBuilder.Build(content, Order(), Reader).ShowPaymentAccount);
        Assert.False(QuotationPdfSectionsBuilder.Build(PdfContent.Empty, Document(), Reader).ShowPaymentAccount);
    }

    // Owner 2026-11-16: el despliegue no regenera ningún PDF, así que la cuenta no entra a la huella; el PDF guardado se vence
    // al guardar un cambio de la cuenta (`InvalidateQuotationPdfsAsync`).
    [Fact]
    public void FingerprintIsUnchangedByThePaymentAccount()
    {
        var content = PdfContent.Empty with { Terms = Section(PdfSectionKey.Terms) };
        var on = content with { PaymentAccount = new PdfPaymentAccount(true, true, true) };
        var off = content with { PaymentAccount = PdfPaymentAccount.Empty };

        Assert.Equal(
            QuotationPdfSectionsBuilder.Fingerprint(off, QuotationPdfKind.Quotation),
            QuotationPdfSectionsBuilder.Fingerprint(on, QuotationPdfKind.Quotation));
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            QuotationPdfSectionsBuilder.Fingerprint(
                PdfContent.Empty with { PaymentAccount = new PdfPaymentAccount(true, true, true) }, QuotationPdfKind.Quotation));
    }

    [Fact]
    public void VariablesAreResolvedInTitleAndBlocks()
    {
        var content = PdfContent.Empty with
        {
            Terms = new PdfSection(
                PdfSectionKey.Terms, true, true, true, "Términos para {{cliente}}", "<p>Documento <strong>{{ numero }}</strong></p>"),
        };

        var terms = QuotationPdfSectionsBuilder.Build(content, Document(), Reader).Terms!;

        Assert.Equal("Términos para María Pérez", terms.Title);
        var paragraph = Assert.Single(terms.Blocks);
        Assert.Equal(RichTextBlockKind.Paragraph, paragraph.Kind);
        var bold = Assert.Single(paragraph.Runs!, run => run.Bold);
        Assert.Equal("QUO-2026-0042", bold.Text);
        Assert.DoesNotContain(paragraph.Runs!, run => run.Text.Contains("{{", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidUntilIsEmptyOnAnOrder()
    {
        Assert.Equal("30 de noviembre de 2026", QuotationPdfSectionsBuilder.ValuesOf(Document()).ValidUntil);
        Assert.Null(QuotationPdfSectionsBuilder.ValuesOf(Order()).ValidUntil);
        Assert.Equal(
            string.Empty,
            QuotationPdfVariables.Fill("{{vigencia}}", QuotationPdfSectionsBuilder.ValuesOf(Order())));
    }

    [Fact]
    public void CompanyIsEmptyWithoutABillingAccount()
    {
        var values = QuotationPdfSectionsBuilder.ValuesOf(Document() with { BillingAccount = null });

        Assert.Null(values.Company);
        Assert.Null(values.CompanyTaxId);
        Assert.Equal(string.Empty, QuotationPdfVariables.Fill("{{empresa}}{{nit_empresa}}", values));
    }

    // La fecha sale como la imprime `fecha()` de `quotation.typ`: día sin cero, mes en minúscula, año.
    [Fact]
    public void TheDatesAreFormattedLikeTheTemplate()
    {
        var values = QuotationPdfSectionsBuilder.ValuesOf(Document() with { IssuedOn = new DateOnly(2026, 1, 5) });

        Assert.Equal("5 de enero de 2026", values.IssuedOn);
        Assert.Equal("Comercial Andina S.A.S.", values.Company);
        Assert.Equal("900123456-7", values.CompanyTaxId);
        Assert.Equal("María Pérez", values.Customer);
        Assert.Equal("QUO-2026-0042", values.Number);
        Assert.Equal("Laura Gómez", values.Advisor);
    }

    // Como `importe()` de la plantilla: símbolo, espacio, miles con el separador del tenant y, con decimales, siempre dos.
    [Fact]
    public void TheTotalIsFormattedLikeTheTemplate()
    {
        var cop = QuotationPdfSectionsBuilder.ValuesOf(Document() with
        {
            Currency = "COP",
            Symbol = "$",
            Decimals = 0,
            Total = 1_250_000m,
            ThousandsSeparator = ".",
            DecimalSeparator = ",",
        });
        var usd = QuotationPdfSectionsBuilder.ValuesOf(Document() with
        {
            Currency = "USD",
            Symbol = Currencies.Get("USD").Symbol,
            Decimals = 2,
            Total = 1250.5m,
            ThousandsSeparator = ",",
            DecimalSeparator = ".",
        });

        Assert.Equal("$ 1.250.000", cop.Total);
        Assert.Equal(Currencies.Get("USD").Symbol + " 1,250.50", usd.Total);
    }

    // La plantilla redondea en coma flotante (`calc.round(valor * 100)`): 1,005 es 1,00499… en double y sale 1,00, no 1,01;
    // 2,675 × 100 en double da justo 267,5 y sale 2,68. Valores medidos con typst 0.15.1 sobre `json(bytes(...))`.
    // `{{total}}` tiene que decir lo mismo que la fila del total en el mismo PDF.
    [Theory]
    [InlineData("1.005", "1.00")]
    [InlineData("2.675", "2.68")]
    [InlineData("1250.5", "1,250.50")]
    public void TheTotalRoundsHalfCentsLikeTheTemplate(string total, string expected)
    {
        var usd = QuotationPdfSectionsBuilder.ValuesOf(Document() with
        {
            Currency = "USD",
            Symbol = Currencies.Get("USD").Symbol,
            Decimals = 2,
            Total = decimal.Parse(total, CultureInfo.InvariantCulture),
            ThousandsSeparator = ",",
            DecimalSeparator = ".",
        });

        Assert.Equal(Currencies.Get("USD").Symbol + " " + expected, usd.Total);
    }

    [Fact]
    public void TheFingerprintChangesWithTheContentAndNotWithTheOtherKind()
    {
        var content = PdfContent.Empty with
        {
            Terms = Section(PdfSectionKey.Terms),
            DeliveryProcess = Section(PdfSectionKey.DeliveryProcess, inQuotation: false, inOrder: true),
        };
        var editedTerms = content with { Terms = content.Terms with { ContentHtml = "<p>Otro texto</p>" } };
        var editedOrderOnly = content with { DeliveryProcess = content.DeliveryProcess with { Title = "Otro título" } };

        var quotation = QuotationPdfSectionsBuilder.Fingerprint(content, QuotationPdfKind.Quotation);

        Assert.Equal(quotation, QuotationPdfSectionsBuilder.Fingerprint(content, QuotationPdfKind.Quotation));
        Assert.Matches("^[0-9a-f]{64}$", quotation);
        Assert.NotEqual(quotation, QuotationPdfSectionsBuilder.Fingerprint(editedTerms, QuotationPdfKind.Quotation));
        Assert.Equal(quotation, QuotationPdfSectionsBuilder.Fingerprint(editedOrderOnly, QuotationPdfKind.Quotation));
        Assert.NotEqual(
            QuotationPdfSectionsBuilder.Fingerprint(content, QuotationPdfKind.Order),
            QuotationPdfSectionsBuilder.Fingerprint(editedOrderOnly, QuotationPdfKind.Order));
    }

    // Apagar una sección cambia lo que se imprime aunque el texto siga guardado: la huella también cambia.
    [Fact]
    public void TurningASectionOffChangesTheFingerprint()
    {
        var content = PdfContent.Empty with { Terms = Section(PdfSectionKey.Terms) };

        Assert.NotEqual(
            QuotationPdfSectionsBuilder.Fingerprint(content, QuotationPdfKind.Quotation),
            QuotationPdfSectionsBuilder.Fingerprint(content with { Terms = content.Terms with { Enabled = false } }, QuotationPdfKind.Quotation));
    }

    [Fact]
    public void WithoutSectionsTheFingerprintIsTheHashOfNothing() =>
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            QuotationPdfSectionsBuilder.Fingerprint(PdfContent.Empty, QuotationPdfKind.Quotation));

    private static PdfSection Section(PdfSectionKey key, bool inQuotation = true, bool inOrder = true) =>
        new(key, true, inQuotation, inOrder, "Título", "<p>Texto</p>");

    private static QuotationPdfDocument Order() =>
        Document() with { Kind = QuotationPdfKind.Order, ValidUntil = null };

    private static QuotationPdfDocument Document() => new(
        QuotationNumber: "QUO-2026-0042",
        IssuedOn: new DateOnly(2026, 11, 16),
        ValidUntil: new DateOnly(2026, 11, 30),
        CustomerName: "María Pérez",
        CustomerCuc: "CUC-0042",
        CustomerContact: string.Empty,
        CustomerLocation: string.Empty,
        Billing: new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty),
        Shipping: new QuotationPdfParty(true, string.Empty, string.Empty, string.Empty),
        IsStorePickup: false,
        AdvisorLabel: "Laura Gómez",
        Currency: "COP",
        Symbol: "$",
        Decimals: 0,
        BillingAccount: new QuotationPdfBillingAccount(
            "Comercial Andina S.A.S.", "900123456-7", "NIT", string.Empty, string.Empty, "Bancolombia", "123", "COP"),
        PaymentMethod: null,
        Items: [],
        Subtotal: 1_050_420m,
        DiscountAmount: 0m,
        TaxPercentage: 19m,
        TaxAmount: 199_580m,
        Total: 1_250_000m,
        RetentionAmount: 0m,
        Withholdings: [],
        NetTotal: 1_250_000m,
        CustomerVatSurplus: false,
        Logo: null);
}
