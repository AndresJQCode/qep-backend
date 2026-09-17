namespace Modules.Storage.Application;

/// <summary>
/// Reduce y recodifica la imagen de un comprobante de pago al completar la subida (spec 2026-09-16,
/// D7 y D8). Es un puerto propio y no <see cref="IImageVariantGenerator"/> porque el resultado
/// **reemplaza** al original en staging/ en vez de sumarse como variante.
/// </summary>
public interface IPaymentProofImageProcessor
{
    /// <summary>Si el tipo es una imagen que se procesa: JPG, PNG o WebP. Un PDF queda tal cual
    /// (D1).</summary>
    bool Supports(string mimeType);

    /// <summary>La imagen procesada. Lanza <c>StorageDomainException</c> con
    /// <c>storage.image.invalid</c> o <c>storage.image.dimensions_too_large</c> cuando no se puede
    /// procesar.</summary>
    Task<ProcessedPaymentProofImage> ProcessAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>El contenido que reemplaza al original, con el tipo y la extensión que le corresponden.</summary>
public sealed record ProcessedPaymentProofImage(
    byte[] Content,
    string MimeType,
    string Extension,
    int Width,
    int Height);
