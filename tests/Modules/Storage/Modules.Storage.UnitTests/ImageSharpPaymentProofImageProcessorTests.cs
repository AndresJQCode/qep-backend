using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Modules.Storage.UnitTests;

/// <summary>
/// La imagen de un comprobante de pago (spec 2026-09-16, D7): lado mayor de hasta 2000 px, sin
/// agrandar, WebP con pérdida y calidad 80, y sin EXIF, ICC ni XMP; una imagen corrupta se rechaza
/// con el mismo código que usa la miniatura.
/// </summary>
public sealed class ImageSharpPaymentProofImageProcessorTests
{
    private readonly ImageSharpPaymentProofImageProcessor _processor = new();

    [Theory]
    [InlineData(3000, 1500, 2000, 1000)]
    [InlineData(1500, 3000, 1000, 2000)]
    public async Task ALargeImageIsReducedToALongSideOf2000(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        var processed = await _processor.ProcessAsync(
            await PngAsync(width, height), TestContext.Current.CancellationToken);

        var info = Image.Identify(processed.Content);
        Assert.Equal(expectedWidth, info.Width);
        Assert.Equal(expectedHeight, info.Height);
        Assert.Equal(expectedWidth, processed.Width);
        Assert.Equal(expectedHeight, processed.Height);
    }

    // D7: sin agrandar. ResizeMode.Max de ImageSharp sí agrandaría esta imagen.
    [Fact]
    public async Task ASmallImageIsNotEnlarged()
    {
        var processed = await _processor.ProcessAsync(
            await PngAsync(800, 600), TestContext.Current.CancellationToken);

        var info = Image.Identify(processed.Content);
        Assert.Equal(800, info.Width);
        Assert.Equal(600, info.Height);
    }

    // D7: el resultado es exactamente la codificación WebP con pérdida y calidad 80 de la misma
    // imagen, y no la de calidad 100. La imagen es ruidosa para que las dos calidades difieran.
    [Fact]
    public async Task TheResultIsLossyWebpAtQuality80()
    {
        var source = await PngAsync(640, 480, noisy: true);

        var processed = await _processor.ProcessAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal("image/webp", processed.MimeType);
        Assert.Equal(".webp", processed.Extension);
        Assert.Equal("RIFF"u8.ToArray(), processed.Content[..4]);
        Assert.Equal("WEBP"u8.ToArray(), processed.Content[8..12]);
        Assert.Equal(await EncodeAsync(source, quality: 80), processed.Content);
        Assert.NotEqual(await EncodeAsync(source, quality: 100), processed.Content);
    }

    // Una foto de celular trae la ubicación en el EXIF, y el comprobante termina en un bucket público
    // (D3): el WebP sale sin EXIF, ICC ni XMP. La fuente sólo lleva EXIF con GPS, porque armar a mano
    // un perfil ICC válido es frágil; el resultado se revisa igual para los tres.
    [Fact]
    public async Task TheResultCarriesNoExifIccOrXmpProfile()
    {
        var source = await JpegWithGpsAsync();
        // Sin esto la prueba pasaría en falso si el JPEG no guardara el EXIF.
        Assert.NotNull(Image.Identify(source).Metadata.ExifProfile);

        var processed = await _processor.ProcessAsync(source, TestContext.Current.CancellationToken);

        var metadata = Image.Identify(processed.Content).Metadata;
        Assert.Null(metadata.ExifProfile);
        Assert.Null(metadata.IccProfile);
        Assert.Null(metadata.XmpProfile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACorruptImageIsRejectedAsInvalid(bool withPngSignature)
    {
        byte[] corrupt = withPngSignature
            ? [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. "not really a png"u8.ToArray()]
            : "not an image at all"u8.ToArray();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            _processor.ProcessAsync(corrupt, TestContext.Current.CancellationToken));

        Assert.Equal("storage.image.invalid", error.Code);
    }

    // D1: un PDF queda tal cual; sólo las imágenes se procesan.
    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("IMAGE/PNG", true)]
    [InlineData("image/webp", true)]
    [InlineData("application/pdf", false)]
    public void SupportsOnlyTheProofImageTypes(string mimeType, bool expected)
    {
        Assert.Equal(expected, _processor.Supports(mimeType));
    }

    private static async Task<byte[]> PngAsync(int width, int height, bool noisy = false)
    {
        using var image = new Image<Rgba32>(width, height, Color.CornflowerBlue);
        if (noisy)
        {
            // Un patrón determinista, sin Random: la prueba tiene que dar lo mismo siempre.
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    image[x, y] = new Rgba32(
                        (byte)(x * 31 ^ y * 17), (byte)(x * 7 + y * 13), (byte)(x ^ y), 255);
                }
            }
        }

        await using var output = new MemoryStream();
        await image.SaveAsPngAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static async Task<byte[]> JpegWithGpsAsync()
    {
        using var image = new Image<Rgba32>(320, 240, Color.CornflowerBlue);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(4u, 1u), new Rational(36u, 1u), new Rational(0u, 1u) });
        image.Metadata.ExifProfile = exif;

        await using var output = new MemoryStream();
        await image.SaveAsJpegAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private static async Task<byte[]> EncodeAsync(byte[] png, int quality)
    {
        using var image = Image.Load(png);
        await using var output = new MemoryStream();
        await image.SaveAsWebpAsync(
            output,
            new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = quality },
            TestContext.Current.CancellationToken);
        return output.ToArray();
    }
}
