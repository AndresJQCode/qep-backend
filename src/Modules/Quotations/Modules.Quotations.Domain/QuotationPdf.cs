namespace Modules.Quotations.Domain;

/// <summary>
/// El PDF ya generado de una cotización, y con qué versión del agregado —y qué logo del tenant—
/// se generó.
///
/// Es un artefacto **derivado**: se puede reconstruir en cualquier momento desde la cotización,
/// así que no vive en su agregado ni tiene invariantes de negocio propias. Existe para no pagar
/// una llamada a `qcode-pdf` cada vez que alguien exporta o reenvía un documento que no cambió.
///
/// Uno por cotización: la clave primaria es <see cref="QuotationId"/>. Regenerar pisa la fila y
/// el objeto anterior queda huérfano en el bucket, donde lo limpia la regla de lifecycle.
/// </summary>
public sealed class QuotationPdf
{
    private QuotationPdf()
    {
    }

    private QuotationPdf(
        QuotationId quotationId,
        Guid tenantId,
        string storageKey,
        long quotationVersion,
        Guid? logoFileId,
        string? numberFormat,
        string? contentFingerprint,
        DateTimeOffset generatedAt)
    {
        QuotationId = quotationId;
        TenantId = tenantId;
        StorageKey = storageKey;
        QuotationVersion = quotationVersion;
        LogoFileId = logoFileId;
        NumberFormat = numberFormat;
        ContentFingerprint = contentFingerprint;
        GeneratedAt = generatedAt;
    }

    public QuotationId QuotationId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>La clave en el bucket **privado**, que es el canónico. La copia pública que se
    /// le entrega a Meta se crea en cada envío y no se registra acá: es descartable.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    /// <summary><c>Quotation.Version</c> al momento de generar. No hace falta un mecanismo
    /// propio de invalidación: el agregado ya lo incrementa en cada cambio.</summary>
    public long QuotationVersion { get; private set; }

    /// <summary>El <c>LogoFileId</c> vigente del tenant al momento de generar, o null sin logo
    /// (spec 2026-09-19, decisión 10). Compara por <c>!=</c> en <see cref="IsStaleFor"/> porque,
    /// a diferencia de <see cref="QuotationVersion"/>, no tiene orden.</summary>
    public Guid? LogoFileId { get; private set; }

    /// <summary>El formato numérico del tenant con el que se imprimió (<c>1.234,56</c> o <c>1,234.56</c>), o
    /// null en las filas anteriores a la columna, que siempre salieron con el colombiano. Cambiarlo en el
    /// tenant vence el PDF igual que el logo (spec 2026-10-23, D-R3).</summary>
    public string? NumberFormat { get; private set; }

    /// <summary>La huella de las secciones configurables que salieron en el documento (spec 2026-11-16 §5.4), o null en las filas
    /// anteriores a la columna, que se imprimieron con los textos fijos de la plantilla. Se compara por <c>!=</c>, como el logo, y un
    /// null queda vencido: así ningún PDF guardado sigue mostrando un aviso que el tenant no configuró.</summary>
    public string? ContentFingerprint { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    // Es `TenantNumberFormats.CommaDecimal` de Tenancy, que este dominio no referencia: lo que se
    // guardó antes de la columna se imprimió siempre así. Lo ata a la constante de Tenancy la prueba
    // `ALegacyPdfWithoutFormatCountsAsCommaDecimal`.
    private const string LegacyNumberFormat = "1.234,56";

    public static QuotationPdf Generate(
        QuotationId quotationId,
        Guid tenantId,
        string storageKey,
        long quotationVersion,
        Guid? logoFileId,
        string? numberFormat,
        string contentFingerprint,
        DateTimeOffset generatedAt) =>
        new(quotationId, tenantId, storageKey, quotationVersion, logoFileId, numberFormat, contentFingerprint, generatedAt);

    /// <summary>
    /// La versión se compara con <c>&lt;</c> y no con <c>!=</c>: un PDF que quedara **adelante**
    /// de la cotización —una restauración de base, una escritura fuera de orden— no se arregla
    /// regenerándolo, y tratarlo como obsoleto lo haría regenerar en cada pedido, para siempre. El
    /// logo y la huella del contenido se comparan por <c>!=</c> porque no tienen ese mismo orden.
    /// </summary>
    public bool IsStaleFor(long quotationVersion, Guid? logoFileId, string numberFormat, string contentFingerprint) =>
        QuotationVersion < quotationVersion
        || LogoFileId != logoFileId
        || (NumberFormat ?? LegacyNumberFormat) != numberFormat
        || ContentFingerprint != contentFingerprint;

    public void Regenerate(
        string storageKey,
        long quotationVersion,
        Guid? logoFileId,
        string numberFormat,
        string contentFingerprint,
        DateTimeOffset generatedAt)
    {
        StorageKey = storageKey;
        QuotationVersion = quotationVersion;
        LogoFileId = logoFileId;
        NumberFormat = numberFormat;
        ContentFingerprint = contentFingerprint;
        GeneratedAt = generatedAt;
    }
}
