using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-20 D10: el <c>Range</c> de <c>GET …/media/{id}</c> (RFC 9110 §14). Un solo rango; lo que no se entiende se
/// ignora y se sirve entero (200), como permite la RFC; lo que no cabe en el objeto es 416.</summary>
public sealed class MediaByteRangeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("bytes=0-99", 0L, 99L)]
    [InlineData("bytes=100-", 100L, null)]
    [InlineData("bytes=-50", null, 50L)]
    [InlineData(" Bytes = 5-5 ", 5L, 5L)]
    public void ASingleRangeIsParsed(string header, long? from, long? to) =>
        Assert.Equal(new MediaByteRangeRequest(from, to), MediaByteRange.Parse(header));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("items=0-1")]
    [InlineData("bytes=5-1")]
    [InlineData("bytes=-")]
    [InlineData("bytes=a-b")]
    [InlineData("bytes=99999999999999999999-")]
    public void AHeaderThatIsMissingMultipleOrNotUnderstoodIsIgnored(string? header) => Assert.Null(MediaByteRange.Parse(header));

    [Theory]
    [InlineData("bytes=0-99", 1000L, 0L, 99L)]
    [InlineData("bytes=900-5000", 1000L, 900L, 999L)]
    [InlineData("bytes=100-", 1000L, 100L, 999L)]
    [InlineData("bytes=-50", 1000L, 950L, 999L)]
    [InlineData("bytes=-5000", 1000L, 0L, 999L)]
    public void ARangeIsResolvedAgainstTheObjectSize(string header, long total, long from, long to) =>
        Assert.Equal(new MediaRangeDto(from, to, total), MediaByteRange.Resolve(MediaByteRange.Parse(header)!.Value, total));

    [Theory]
    [InlineData("bytes=1000-", 1000L)]
    [InlineData("bytes=1000-2000", 1000L)]
    [InlineData("bytes=-0", 1000L)]
    [InlineData("bytes=0-", 0L)]
    public void ARangeOutsideTheObjectIsNotSatisfiable(string header, long total)
    {
        var resolved = MediaByteRange.Resolve(MediaByteRange.Parse(header)!.Value, total);

        Assert.False(resolved.Satisfiable);
        Assert.Equal(total, resolved.Total);
    }

    // Los dobles del almacén no implementan la lectura por rango: la de la interfaz corta el objeto entero y es la misma respuesta.
    [Fact]
    public async Task TheDefaultRangedReadOfTheStoreSlicesTheWholeObject()
    {
        var store = new InMemoryMediaStore();
        store.Objects["k"] = ([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], "video/mp4");
        IMessagingMediaStore port = store;

        var size = await port.SizeOfAsync("k", Ct);
        var opened = await port.OpenReadRangeAsync("k", 3, 6, Ct);

        Assert.Equal(10L, size);
        Assert.NotNull(opened);
        await using var content = opened.Content;
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, Ct);
        Assert.Equal([3, 4, 5, 6], buffer.ToArray());
        Assert.Equal((4L, "video/mp4"), (opened.Length, opened.ContentType));
        Assert.Null(await port.SizeOfAsync("missing", Ct));
        Assert.Null(await port.OpenReadRangeAsync("missing", 0, 1, Ct));
    }
}
