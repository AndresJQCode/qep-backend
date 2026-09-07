namespace Modules.Quotations.Domain;

/// <summary>
/// El PDF ya generado de una cotización, y con qué versión del agregado se generó.
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
        DateTimeOffset generatedAt)
    {
        QuotationId = quotationId;
        TenantId = tenantId;
        StorageKey = storageKey;
        QuotationVersion = quotationVersion;
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

    public DateTimeOffset GeneratedAt { get; private set; }

    public static QuotationPdf Generate(
        QuotationId quotationId,
        Guid tenantId,
        string storageKey,
        long quotationVersion,
        DateTimeOffset generatedAt) =>
        new(quotationId, tenantId, storageKey, quotationVersion, generatedAt);

    /// <summary>
    /// Con <c>&lt;</c> y no con <c>!=</c>: un PDF que quedara **adelante** de la cotización
    /// —una restauración de base, una escritura fuera de orden— no se arregla regenerándolo, y
    /// tratarlo como obsoleto lo haría regenerar en cada pedido, para siempre.
    /// </summary>
    public bool IsStaleFor(long quotationVersion) => QuotationVersion < quotationVersion;

    public void Regenerate(string storageKey, long quotationVersion, DateTimeOffset generatedAt)
    {
        StorageKey = storageKey;
        QuotationVersion = quotationVersion;
        GeneratedAt = generatedAt;
    }
}
