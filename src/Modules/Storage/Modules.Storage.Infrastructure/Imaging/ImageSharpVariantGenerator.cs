using Modules.Storage.Application;
using Modules.Storage.Domain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Modules.Storage.Infrastructure.Imaging;

internal sealed class ImageSharpVariantGenerator : IImageVariantGenerator
{
    private const int ThumbnailMaxPixels = 320;
    private const long MaximumSourcePixels = 40_000_000;

    public bool Supports(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<GeneratedFileVariant>> GenerateAsync(
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
            image.Mutate(context => context
                .AutoOrient()
                .Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(ThumbnailMaxPixels, ThumbnailMaxPixels),
                    Sampler = KnownResamplers.Lanczos3,
                }));
            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null;

            await using var output = new MemoryStream();
            await image.SaveAsWebpAsync(
                output,
                new WebpEncoder { Quality = 80 },
                cancellationToken);
            var bytes = output.ToArray();
            return
            [
                new GeneratedFileVariant(
                    "thumbnail",
                    bytes,
                    "image/webp",
                    "webp",
                    image.Width,
                    image.Height),
            ];
        }
        catch (Exception exception) when (
            exception is UnknownImageFormatException or InvalidImageContentException)
        {
            throw InvalidImage();
        }
    }

    private static StorageDomainException InvalidImage() =>
        new(
            "storage.image.invalid",
            "The uploaded image could not be decoded.");
}
