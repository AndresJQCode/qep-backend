using Modules.Siigo.Application.Products;
using Modules.Siigo.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>Spec 2026-11-18 §5.3, paso 6: «Vincular por código» empareja sin distinguir mayúsculas y sin espacios.</summary>
public sealed class SiigoCodeMatchTests
{
    [Fact]
    public void ACodeWithSpacesAndLowercaseMatchesItsCleanTwin()
    {
        Assert.Equal(SiigoCodeMatch.Normalize("AB-1"), SiigoCodeMatch.Normalize(" ab-1 "));
        Assert.Equal("AB-1", SiigoCodeMatch.Normalize(" ab-1 "));
    }

    [Fact]
    public void SpacesInsideTheCodeAreIgnoredToo() => Assert.Equal("AB1", SiigoCodeMatch.Normalize("a b 1"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingCodeMatchesNothing(string? code) => Assert.Null(SiigoCodeMatch.Normalize(code));

    [Fact]
    public void DifferentCodesDoNotMatch() => Assert.NotEqual(SiigoCodeMatch.Normalize("AB-1"), SiigoCodeMatch.Normalize("AB-2"));

    [Fact]
    public void LinkingManyVariantsBumpsTheVersionOnceAndNothingWhenEmpty()
    {
        var settings = SiigoSettings.Create(Guid.CreateVersion7(), Guid.CreateVersion7());
        var member = Guid.CreateVersion7();
        var at = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        settings.LinkVariants([], SiigoLinkOrigin.AutoCode, member, at);
        Assert.Equal(0, settings.Version);

        settings.LinkVariants(
            [new SiigoVariantLinkInput(Guid.CreateVersion7(), "AB-1", "p-1", "Uno"), new SiigoVariantLinkInput(Guid.CreateVersion7(), "AB-2", "p-2", "Dos")],
            SiigoLinkOrigin.AutoCode,
            member,
            at);

        Assert.Equal(1, settings.Version);
        Assert.Equal(2, settings.VariantLinks.Count);
        Assert.All(settings.VariantLinks, link => Assert.Equal(SiigoLinkOrigin.AutoCode, link.Origin));
    }
}
