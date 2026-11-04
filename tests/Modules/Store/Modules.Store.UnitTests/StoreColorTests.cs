using Modules.Store.Domain;

namespace Modules.Store.UnitTests;

public sealed class StoreColorTests
{
    private static StoreColor Parse(string hex)
    {
        Assert.True(StoreColor.TryParse(hex, out var color));
        return color;
    }

    private static double Hue(StoreColor c)
    {
        double r = c.R / 255d, g = c.G / 255d, b = c.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var d = max - Math.Min(r, Math.Min(g, b));
        if (d == 0d)
        {
            return 0d;
        }

        var h = max == r ? ((g - b) / d) % 6d : max == g ? ((b - r) / d) + 2d : ((r - g) / d) + 4d;
        return (h * 60d + 360d) % 360d;
    }

    [Theory]
    [InlineData("#1f4fd8")]
    [InlineData("#ABCDEF")]
    public void AHexColorParsesAndAnswersUppercase(string raw)
    {
        Assert.True(StoreColor.TryParse(raw, out var color));
        Assert.Equal(raw.ToUpperInvariant(), color.ToHex());
    }

    [Theory]
    [InlineData("1F4FD8")]
    [InlineData("#1F4FD")]
    [InlineData("#GGGGGG")]
    [InlineData("blue")]
    [InlineData(null)]
    public void AMalformedHexIsRejected(string? raw) => Assert.False(StoreColor.TryParse(raw, out _));

    [Fact]
    public void LuminanceMatchesWcagReferenceValues()
    {
        Assert.Equal(1.0, Parse("#FFFFFF").RelativeLuminance(), 6);
        Assert.Equal(0.0, Parse("#000000").RelativeLuminance(), 6);
        Assert.Equal(0.1845, Parse("#777777").RelativeLuminance(), 3);
    }

    [Fact]
    public void ContrastOfBlackOnWhiteIs21() =>
        Assert.Equal(21.0, StoreColor.Contrast(Parse("#000000"), Parse("#FFFFFF")), 6);

    [Fact]
    public void ContrastIsSymmetric()
    {
        var a = Parse("#1F4FD8");
        var b = Parse("#FFD400");
        Assert.Equal(StoreColor.Contrast(a, b), StoreColor.Contrast(b, a), 12);
    }

    [Fact]
    public void DarkenKeepsTheHueAndLowersTheLightness()
    {
        var yellow = Parse("#FFD400");
        var darker = yellow.Darken(0.30);

        Assert.True(darker.RelativeLuminance() < yellow.RelativeLuminance());
        Assert.InRange(Math.Abs(Hue(darker) - Hue(yellow)), 0d, 1d);
    }

    [Fact]
    public void DarkenNeverGoesBelowBlack() => Assert.Equal("#000000", Parse("#1F4FD8").Darken(5).ToHex());

    [Fact]
    public void AutoOnPrimaryPicksTheHigherContrast()
    {
        Assert.Equal("#111111", StoreColorScheme.Resolve(Parse("#FFD400"), null, null).OnPrimary.ToHex());
        Assert.Equal("#FFFFFF", StoreColorScheme.Resolve(Parse("#1F4FD8"), null, null).OnPrimary.ToHex());
    }

    [Fact]
    public void AutoAccentTextReachesFourAndAHalfOnWhiteKeepingTheHue()
    {
        var primary = Parse("#FFD400");
        var accent = StoreColorScheme.Resolve(primary, null, null).AccentText;

        Assert.True(StoreColor.Contrast(accent, StoreColor.White) >= 4.5);
        Assert.InRange(Math.Abs(Hue(accent) - Hue(primary)), 0d, 1d);
    }

    [Fact]
    public void AnAccentPrimaryThatAlreadyContrastsIsKeptAsIs()
    {
        var primary = Parse("#1F4FD8");
        Assert.Equal(primary, StoreColorScheme.Resolve(primary, null, null).AccentText);
    }

    [Fact]
    public void AWhitePrimaryIsDarkenedUntilItReachesFourAndAHalfOnWhite()
    {
        var accent = StoreColorScheme.Resolve(Parse("#FFFFFF"), null, null).AccentText;

        Assert.Equal("#757575", accent.ToHex());
        Assert.True(StoreColor.Contrast(accent, StoreColor.White) >= 4.5);
    }

    [Fact]
    public void TheShownRatioIsFlooredSoItIsBelowFourAndAHalfExactlyWhenFlagged()
    {
        // 4.4986 redondea a 4.50 y aun así no cumple: la razón mostrada se trunca a 4.49.
        var checks = StoreColorScheme.Resolve(Parse("#FFFFFF"), Parse("#FFFFFF"), Parse("#76767F")).Checks;

        Assert.True(checks.AccentTextLowContrast);
        Assert.Equal(4.49, checks.AccentTextContrast);
        Assert.True(checks.AccentTextContrast < StoreColorScheme.MinimumContrast);
    }

    [Fact]
    public void TheFlagAndTheShownRatioAgreeForEveryGrayOverride()
    {
        for (var v = 0; v <= 255; v++)
        {
            var gray = new StoreColor((byte)v, (byte)v, (byte)v);
            var checks = StoreColorScheme.Resolve(Parse("#FFFFFF"), null, gray).Checks;

            Assert.Equal(checks.AccentTextLowContrast, checks.AccentTextContrast < StoreColorScheme.MinimumContrast);
        }
    }

    [Fact]
    public void ALowContrastOverrideIsKeptAndFlagged()
    {
        var resolved = StoreColorScheme.Resolve(Parse("#FFD400"), Parse("#FFFFFF"), Parse("#FFE680"));

        Assert.Equal("#FFFFFF", resolved.OnPrimary.ToHex());
        Assert.Equal("#FFE680", resolved.AccentText.ToHex());
        Assert.True(resolved.Checks.OnPrimaryLowContrast);
        Assert.True(resolved.Checks.AccentTextLowContrast);
    }

    [Fact]
    public void AGoodAutomaticSchemeIsNotFlaggedAndRatiosHaveTwoDecimals()
    {
        var checks = StoreColorScheme.Resolve(Parse("#1F4FD8"), null, null).Checks;

        Assert.False(checks.OnPrimaryLowContrast);
        Assert.False(checks.AccentTextLowContrast);
        Assert.Equal(Math.Round(checks.OnPrimaryContrast, 2), checks.OnPrimaryContrast);
        Assert.Equal(Math.Round(checks.AccentTextContrast, 2), checks.AccentTextContrast);
    }
}
