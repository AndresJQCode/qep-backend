using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;

namespace Bootstrapper.UnitTests;

/// <summary>Messaging-media-reply R12: toda imagen del asesor sale como JPEG q80, con el lado mayor a lo sumo en 1600 px,
/// sin metadatos y sin ampliarla. Lo que no decodifica como jpeg/png (o pasa de 40 MP) es <c>null</c>.</summary>
public sealed class ImageSharpMessagingImageCompressorTests
{
    private readonly ImageSharpMessagingImageCompressor _compressor = new();

    [Fact]
    public async Task ABigPhotoIsShrunkToTheLongestSideOf1600()
    {
        using var source = Encode(new Image<Rgb24>(4000, 3000, new Rgb24(10, 120, 200)), new JpegEncoder());

        var result = await _compressor.CompressAsync(source, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1600, result.Width);
        Assert.Equal(1200, result.Height);
        var decoded = Image.Identify(result.Content);
        Assert.Equal((1600, 1200), (decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task ASmallImageIsNotEnlargedButIsStillRecompressed()
    {
        var original = Encode(new Image<Rgb24>(800, 600, new Rgb24(200, 30, 30)), new JpegEncoder { Quality = 100 });
        var originalLength = original.Length;
        original.Position = 0;

        var result = await _compressor.CompressAsync(original, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal((800, 600), (result.Width, result.Height));
        Assert.True(result.Content.Length < originalLength, "q80 pesa menos que q100");
        Assert.Equal("Jpeg", Image.Identify(result.Content).Metadata.DecodedImageFormat?.Name, ignoreCase: true);
    }

    [Fact]
    public async Task ExifIsDropped()
    {
        using var image = new Image<Rgb24>(100, 100, new Rgb24(1, 2, 3));
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Make, "PrivateCamera");
        using var source = Encode(image, new JpegEncoder());
        Assert.NotNull(Image.Identify(source.ToArray()).Metadata.ExifProfile);

        var result = await _compressor.CompressAsync(new MemoryStream(source.ToArray()), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Null(Image.Identify(result.Content).Metadata.ExifProfile);
    }

    [Fact]
    public async Task APngWithAlphaIsFlattenedOnWhiteAndSentAsJpeg()
    {
        using var source = Encode(new Image<Rgba32>(64, 64, new Rgba32(0, 0, 0, 0)), new PngEncoder());

        var result = await _compressor.CompressAsync(source, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        using var decoded = Image.Load<Rgb24>(result.Content);
        var pixel = decoded[32, 32];
        Assert.True(pixel.R > 245 && pixel.G > 245 && pixel.B > 245, $"esperaba blanco y salió {pixel}");
    }

    [Fact]
    public async Task CorruptBytesWithAJpegSignatureGiveNull()
    {
        var bytes = new byte[2048];
        Random.Shared.NextBytes(bytes);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;

        Assert.Null(await _compressor.CompressAsync(new MemoryStream(bytes), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABeyondFortyMegapixelImageGivesNull()
    {
        using var source = Encode(new Image<L8>(6500, 6300, new L8(128)), new PngEncoder());

        Assert.Null(await _compressor.CompressAsync(source, TestContext.Current.CancellationToken));
    }

    // El handler le pasa el staging que viene de R2 detrás de un stream que no se puede recorrer: Identify lo consume y sin
    // un búfer propio la decodificación posterior lo encuentra vacío.
    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    public async Task AnUnseekableSourceIsCompressedToo(string format)
    {
        using var encoded = format == "jpeg"
            ? Encode(new Image<Rgb24>(300, 200, new Rgb24(10, 120, 200)), new JpegEncoder())
            : Encode(new Image<Rgb24>(300, 200, new Rgb24(10, 120, 200)), new PngEncoder());
        await using var source = new UnseekableStream(encoded.ToArray());

        var result = await _compressor.CompressAsync(source, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal((300, 200), (result.Width, result.Height));
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private static MemoryStream Encode<TPixel>(Image<TPixel> image, SixLabors.ImageSharp.Formats.ImageEncoder encoder)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using (image)
        {
            var stream = new MemoryStream();
            image.Save(stream, encoder);
            stream.Position = 0;
            return stream;
        }
    }
}
