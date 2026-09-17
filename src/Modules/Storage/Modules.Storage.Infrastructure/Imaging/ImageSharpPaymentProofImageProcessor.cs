using Modules.Storage.Application;
using Modules.Storage.Domain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Modules.Storage.Infrastructure.Imaging;

// Spec 2026-09-16, D7: a 2000 px de lado mayor y calidad 80 el texto de un comprobante sigue
// legible, y el archivo pesa una fracción del original. Mismo tope de píxeles de entrada y mismos
// códigos de error que ImageSharpVariantGenerator: una imagen corrupta falla igual al subirla, sea
// comprobante o no.
internal sealed class ImageSharpPaymentProofImageProcessor : IPaymentProofImageProcessor
{
    internal const int MaxLongSide = 2000;
    internal const int Quality = 80;
    private const long MaximumSourcePixels = 40_000_000;

    private static readonly string[] SupportedMimeTypes = ["image/jpeg", "image/png", "image/webp"];

    private static readonly WebpEncoder Encoder = new()
    {
        FileFormat = WebpFileFormatType.Lossy,
        Quality = Quality,
    };

    public bool Supports(string mimeType) =>
        SupportedMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase);

    public async Task<ProcessedPaymentProofImage> ProcessAsync(
        byte[] content,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = Image.Identify(content)
                ?? throw InvalidImage();
            if ((long)info.Width * info.Height > MaximumSourcePixels)
            {
                throw new StorageDomainException(
                    "storage.image.dimensions_too_large",
                    "The image dimensions exceed the processing limit.");
            }

            using var image = Image.Load(content);
            image.Mutate(context => context.AutoOrient());

            // Sin agrandar (D7): ResizeMode.Max también sube una imagen chica hasta el tope, así que
            // sólo se redimensiona la que lo pasa.
            if (Math.Max(image.Width, image.Height) > MaxLongSide)
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(MaxLongSide, MaxLongSide),
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }

            // Una foto de celular trae GPS y datos del equipo en el EXIF, y el comprobante termina en
            // un bucket público (D3): se descartan, igual que en la miniatura.
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null;

            await using var output = new MemoryStream();
            await image.SaveAsWebpAsync(output, Encoder, cancellationToken);
            return new ProcessedPaymentProofImage(
                output.ToArray(), "image/webp", ".webp", image.Width, image.Height);
        }
        catch (ImageFormatException)
        {
            // Base de UnknownImageFormatException e InvalidImageContentException: un formato
            // desconocido y un contenido roto son, para quien sube, la misma imagen inválida.
            throw InvalidImage();
        }
    }

    private static StorageDomainException InvalidImage() =>
        new(
            "storage.image.invalid",
            "The uploaded image could not be decoded.");
}
