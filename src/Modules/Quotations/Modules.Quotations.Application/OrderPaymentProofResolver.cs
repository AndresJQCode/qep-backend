using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Resuelve cada archivo de comprobante de pago que llega en el comando de conversión (US-14)
/// contra los archivos **del tenant de la cotización**. Mismo criterio que
/// <c>ProductImageResolver</c>: la referencia es blanda, sin FK que
/// la respalde, así que esta comprobación es la única red.
/// </summary>
internal static class OrderPaymentProofResolver
{
    // US-14: "Acepta PDF, JPG, PNG, hasta 10 MB por archivo". WebP desde el 2026-09-16: Storage
    // procesa a WebP toda imagen de un comprobante v2 al completar la subida (spec, D7 y D8).
    private static readonly string[] AllowedMimeTypes =
        ["application/pdf", "image/jpeg", "image/png", "image/webp"];

    private const long MaxSizeBytes = 10 * 1024 * 1024;

    /// <param name="exceptProofId">El comprobante que se reemplaza con este archivo, o null para uno nuevo:
    /// reemplazar un comprobante con su propio archivo no lo cuenta como ya adjunto.</param>
    public static Task ResolveAsync(
        IQuotationFileLookup lookup,
        IOrderRepository orders,
        Guid tenantId,
        Guid fileId,
        OrderPaymentProofId? exceptProofId,
        CancellationToken cancellationToken) =>
        ResolveManyAsync(lookup, orders, tenantId, [fileId], exceptProofId, cancellationToken);

    /// <summary>
    /// Los archivos de un cuerpo en dos lecturas, sin importar cuántos sean: una a Storage y, si alguno es un
    /// comprobante v2, una de uso (consultas por request, fase 2). Las reglas y los códigos son los de siempre, y el
    /// error es el del primer archivo que falla en el orden del cuerpo, como cuando se resolvían de a uno.
    /// </summary>
    public static async Task ResolveManyAsync(
        IQuotationFileLookup lookup,
        IOrderRepository orders,
        Guid tenantId,
        IReadOnlyCollection<Guid> fileIds,
        OrderPaymentProofId? exceptProofId,
        CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0)
        {
            return;
        }

        var files = await lookup.FindManyAsync(tenantId, fileIds, cancellationToken);
        var proofFileIds = files.Values
            .Where(file => file.TenantId == tenantId && file.IsAvailable && file.IsPaymentProof)
            .Select(file => file.FileId)
            .ToArray();
        var inUse = proofFileIds.Length == 0
            ? new HashSet<Guid>()
            : await orders.ListPaymentProofFilesInUseAsync(proofFileIds, exceptProofId, cancellationToken);

        // Un PaymentProof repetido en el mismo cuerpo es «ya adjunto»: el segundo sería otro comprobante con el mismo
        // archivo, que es justo lo que D16 prohíbe, y la base todavía no lo ve.
        var seen = new HashSet<Guid>();
        foreach (var fileId in fileIds)
        {
            files.TryGetValue(fileId, out var file);
            Ensure(file, fileId, tenantId, file is not null && file.IsPaymentProof && (inUse.Contains(fileId) || !seen.Add(fileId)));
        }
    }

    private static void Ensure(QuotationFileRef? file, Guid fileId, Guid tenantId, bool alreadyAttached)
    {
        // Mismo código para "no existe" y "es de otro tenant" -- la frontera de tenant no se
        // distingue desde afuera.
        if (file is null || file.TenantId != tenantId)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_not_found",
                $"File '{fileId}' was not found in this tenant.");
        }

        // Tres casos con el mismo código: la subida no terminó, el comprobante ya se movió al bucket
        // público con otro pedido (spec 2026-09-16, D16), o es un PaymentProof que otro comprobante ya
        // usa aunque todavía no se haya movido (revisión final, I2). Esto último cierra D16 por
        // referencia y no por movimiento: Storage registra una sola clave pública por archivo, así que un
        // segundo adjunto dejaría una copia que ningún retiro llega a borrar. Un archivo User (D13) no
        // tiene la regla: cada adjunto tiene su copia y su original sigue en el bucket privado.
        if (!file.IsAvailable || alreadyAttached)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_not_available",
                "The payment proof file is not available: it has not finished uploading or it is already attached to an order.");
        }

        if (!AllowedMimeTypes.Contains(file.MimeType, StringComparer.OrdinalIgnoreCase))
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_type_not_allowed",
                "The payment proof must be a PDF, JPG, PNG or WEBP file.");
        }

        if (file.SizeBytes > MaxSizeBytes)
        {
            throw new QuotationsDomainException(
                "order.payment_proof.file_too_large",
                "The payment proof cannot exceed 10 MB.");
        }
    }
}
