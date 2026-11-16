namespace Modules.Quotations.Domain;

/// <summary>Las secciones configurables del PDF (spec 2026-11-16 §4.1). Se guardan por nombre: no las reordenes ni las renombres sin
/// migrar el CHECK de <c>quotations.pdf_sections</c>.</summary>
public enum PdfSectionKey
{
    PaymentNotice,
    Terms,
    DeliveryProcess,
}

/// <summary>Una sección del PDF: <paramref name="Title"/> es texto plano y <paramref name="ContentHtml"/> el HTML ya saneado. Apagarla
/// no borra el texto.</summary>
public sealed record PdfSection(PdfSectionKey Key, bool Enabled, bool InQuotation, bool InOrder, string Title, string ContentHtml)
{
    /// <summary>Sale en el documento si está prendida y el flag de ese tipo de documento también.</summary>
    public bool PrintsOn(bool isOrder) => Enabled && (isOrder ? InOrder : InQuotation);

    /// <summary>Lo que lee un tenant sin fila para esa sección: apagada, para los dos documentos y sin texto.</summary>
    public static PdfSection Empty(PdfSectionKey key) => new(key, false, true, true, string.Empty, string.Empty);
}

/// <summary>Si el PDF imprime la cuenta para el pago (empresa, banco y número) de la empresa facturadora (spec 2026-11-16
/// pdf-payment-account). No es una sección: no lleva texto, sólo los mismos tres flags.</summary>
public sealed record PdfPaymentAccount(bool Enabled, bool InQuotation, bool InOrder)
{
    /// <summary>La misma regla que <see cref="PdfSection.PrintsOn"/>.</summary>
    public bool PrintsOn(bool isOrder) => Enabled && (isOrder ? InOrder : InQuotation);

    /// <summary>Lo que lee un tenant sin fila: apagada, con los dos documentos marcados, como <see cref="PdfSection.Empty"/>
    /// (decisión 1 del spec: todos arrancan sin cuenta, salvo Origen Botánico, que la migración siembra).</summary>
    public static PdfPaymentAccount Empty { get; } = new(false, true, true);
}

/// <summary>El contenido configurable del PDF de un tenant: las tres secciones, cada una con su clave, y la cuenta para el pago.</summary>
public sealed record PdfContent(PdfSection PaymentNotice, PdfSection Terms, PdfSection DeliveryProcess, PdfPaymentAccount PaymentAccount)
{
    public const string UnknownVariableCode = "quotation.pdf_content.unknown_variable";

    public static PdfContent Empty { get; } = new(
        PdfSection.Empty(PdfSectionKey.PaymentNotice),
        PdfSection.Empty(PdfSectionKey.Terms),
        PdfSection.Empty(PdfSectionKey.DeliveryProcess),
        PdfPaymentAccount.Empty);

    /// <summary>Las tres secciones en el orden de <see cref="PdfSectionKey"/>; la cuenta para el pago no es una sección.</summary>
    public IEnumerable<PdfSection> All => [PaymentNotice, Terms, DeliveryProcess];

    /// <summary>Rechaza todo <c>{{…}}</c> que no sea una variable de <see cref="QuotationPdfVariables"/>, en el título o en el HTML de
    /// cualquier sección (spec §4.3): un token desconocido llegaría al PDF como texto literal.</summary>
    public static PdfContent Create(
        PdfSection paymentNotice, PdfSection terms, PdfSection deliveryProcess, PdfPaymentAccount paymentAccount)
    {
        ArgumentNullException.ThrowIfNull(paymentNotice);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(deliveryProcess);
        ArgumentNullException.ThrowIfNull(paymentAccount);
        EnsureKey(paymentNotice, PdfSectionKey.PaymentNotice);
        EnsureKey(terms, PdfSectionKey.Terms);
        EnsureKey(deliveryProcess, PdfSectionKey.DeliveryProcess);

        var content = new PdfContent(paymentNotice, terms, deliveryProcess, paymentAccount);
        var unknown = content.All
            .SelectMany(section => QuotationPdfVariables.UnknownIn(section.Title).Concat(QuotationPdfVariables.UnknownIn(section.ContentHtml)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            // El mapa `errors` marca el primer campo con un token desconocido (`Terms.ContentHtml`, `PaymentNotice.Title`), con
            // las mismas claves del validador, para que la consola sepa qué input pintar.
            var (field, fieldUnknown) = FirstFieldWithUnknownVariables(content);
            throw new QuotationsDomainException(
                UnknownVariableCode,
                $"The PDF content uses variables that do not exist: {FormatTokens(unknown)}.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [field] = [$"Unknown variables: {FormatTokens(fieldUnknown)}."],
                });
        }

        return content;
    }

    private static (string Field, IReadOnlyList<string> Unknown) FirstFieldWithUnknownVariables(PdfContent content)
    {
        foreach (var section in content.All)
        {
            var inTitle = QuotationPdfVariables.UnknownIn(section.Title);
            if (inTitle.Count > 0)
            {
                return ($"{section.Key}.{nameof(PdfSection.Title)}", inTitle);
            }

            var inHtml = QuotationPdfVariables.UnknownIn(section.ContentHtml);
            if (inHtml.Count > 0)
            {
                return ($"{section.Key}.{nameof(PdfSection.ContentHtml)}", inHtml);
            }
        }

        throw new InvalidOperationException("No section has unknown variables.");
    }

    private static string FormatTokens(IEnumerable<string> names) =>
        string.Join(", ", names.Select(name => $"{{{{{name}}}}}"));

    private static void EnsureKey(PdfSection section, PdfSectionKey expected)
    {
        if (section.Key != expected)
        {
            throw new ArgumentException($"Expected the {expected} section, got {section.Key}.", nameof(section));
        }
    }
}
