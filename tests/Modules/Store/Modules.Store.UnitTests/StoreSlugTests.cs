using Modules.Store.Domain;

namespace Modules.Store.UnitTests;

public sealed class StoreSlugTests
{
    [Theory]
    [InlineData("Tienda")]
    [InlineData("qep-api")]
    [InlineData("www")]
    public void AReservedSlugIsRejected(string raw)
    {
        Assert.False(StoreSlug.TryNormalize(raw, out _, out var error));
        Assert.Equal(StoreErrorCodes.SlugReserved, error);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac_me")]
    [InlineData(null)]
    public void AMalformedSlugIsRejected(string? raw)
    {
        Assert.False(StoreSlug.TryNormalize(raw, out _, out var error));
        Assert.Equal(StoreErrorCodes.SlugInvalid, error);
    }

    [Fact]
    public void AValidSlugIsTrimmedAndLowercased()
    {
        Assert.True(StoreSlug.TryNormalize("  Acme-Shop ", out var slug, out var error));
        Assert.Equal("acme-shop", slug);
        Assert.Null(error);
    }

    [Fact]
    public void ThereAreNineteenReservedWords() => Assert.Equal(19, StoreSlug.Reserved.Count);

    [Fact]
    public void ALongSlugIsRejectedAtFortyOneCharacters()
    {
        Assert.True(StoreSlug.TryNormalize(new string('a', 40), out _, out _));
        Assert.False(StoreSlug.TryNormalize(new string('a', 41), out _, out var error));
        Assert.Equal(StoreErrorCodes.SlugInvalid, error);
    }
}
