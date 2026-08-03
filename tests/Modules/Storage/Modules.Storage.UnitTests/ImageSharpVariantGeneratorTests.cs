using Modules.Storage.Infrastructure.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Modules.Storage.UnitTests;

public sealed class ImageSharpVariantGeneratorTests
{
    [Fact]
    public async Task GeneratesWebpThumbnailWithinBoundingBox()
    {
        using var source = new Image<Rgba32>(640, 320, Color.CornflowerBlue);
        await using var input = new MemoryStream();
        await source.SaveAsPngAsync(input, TestContext.Current.CancellationToken);
        var generator = new ImageSharpVariantGenerator();

        var variants = await generator.GenerateAsync(
            input.ToArray(), TestContext.Current.CancellationToken);

        var thumbnail = Assert.Single(variants);
        Assert.Equal("thumbnail", thumbnail.Name);
        Assert.Equal("image/webp", thumbnail.MimeType);
        Assert.Equal("webp", thumbnail.Extension);
        Assert.Equal(320, thumbnail.Width);
        Assert.Equal(160, thumbnail.Height);
        Assert.NotEmpty(thumbnail.Content);
    }

    [Theory]
    [InlineData("image/jpeg", true)]
    [InlineData("image/png", true)]
    [InlineData("image/webp", true)]
    [InlineData("application/pdf", false)]
    public void SupportsOnlyImages(string mimeType, bool expected)
    {
        var generator = new ImageSharpVariantGenerator();

        Assert.Equal(expected, generator.Supports(mimeType));
    }
}
