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

/// <summary>El contenido configurable del PDF de un tenant: las tres secciones, cada una con su clave.</summary>
public sealed record PdfContent(PdfSection PaymentNotice, PdfSection Terms, PdfSection DeliveryProcess)
{
    public const string UnknownVariableCode = "quotation.pdf_content.unknown_variable";

    public static PdfContent Empty { get; } = new(
        PdfSection.Empty(PdfSectionKey.PaymentNotice),
        PdfSection.Empty(PdfSectionKey.Terms),
        PdfSection.Empty(PdfSectionKey.DeliveryProcess));

    /// <summary>Las tres en el orden de <see cref="PdfSectionKey"/>.</summary>
    public IEnumerable<PdfSection> All => [PaymentNotice, Terms, DeliveryProcess];

    /// <summary>Rechaza todo <c>{{…}}</c> que no sea una variable de <see cref="QuotationPdfVariables"/>, en el título o en el HTML de
    /// cualquier sección (spec §4.3): un token desconocido llegaría al PDF como texto literal.</summary>
    public static PdfContent Create(PdfSection paymentNotice, PdfSection terms, PdfSection deliveryProcess)
    {
        ArgumentNullException.ThrowIfNull(paymentNotice);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(deliveryProcess);
        EnsureKey(paymentNotice, PdfSectionKey.PaymentNotice);
        EnsureKey(terms, PdfSectionKey.Terms);
        EnsureKey(deliveryProcess, PdfSectionKey.DeliveryProcess);

        var content = new PdfContent(paymentNotice, terms, deliveryProcess);
        var unknown = content.All
            .SelectMany(section => QuotationPdfVariables.UnknownIn(section.Title).Concat(QuotationPdfVariables.UnknownIn(section.ContentHtml)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            throw new QuotationsDomainException(
                UnknownVariableCode,
                $"The PDF content uses variables that do not exist: {string.Join(", ", unknown.Select(name => $"{{{{{name}}}}}"))}.");
        }

        return content;
    }

    private static void EnsureKey(PdfSection section, PdfSectionKey expected)
    {
        if (section.Key != expected)
        {
            throw new ArgumentException($"Expected the {expected} section, got {section.Key}.", nameof(section));
        }
    }
}
