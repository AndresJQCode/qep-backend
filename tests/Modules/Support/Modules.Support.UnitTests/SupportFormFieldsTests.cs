using Modules.Support.Domain;

namespace Modules.Support.UnitTests;

// Los campos del formulario según el documento (spec §9.3, plan F18): una sola regla para dibujar y para exigir.
public sealed class SupportFormFieldsTests
{
    private static readonly Guid TenantId = Guid.Parse("0193a000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 15, 0, 0, TimeSpan.Zero);

    private static SupportFormVersion Form(
        bool showDocument = true, bool showAddress = true, bool showLandline = true, bool businessNameRequired = true) =>
        SupportFormVersion.Create(
            Guid.CreateVersion7(), TenantId, 1, isPublished: true, showDocument, showAddress, showLandline, businessNameRequired,
            allowAttachments: false, accentColor: null, introText: null, successMessage: null, Guid.CreateVersion7(), Now);

    private static Dictionary<string, bool> Fields(SupportFormVariant variant, SupportFormVersion form) =>
        SupportFormFields.For(variant, form).ToDictionary(field => field.Key, field => field.Required);

    [Fact]
    public void NaturalAsksNamesNotBusinessName()
    {
        var fields = Fields(SupportFormVariant.Natural, Form());

        Assert.Equal(
            [
                SupportFormFields.DocumentType, SupportFormFields.DocumentNumber, SupportFormFields.FirstNames,
                SupportFormFields.LastNames, SupportFormFields.Address, SupportFormFields.LandlinePhone, SupportFormFields.MobilePhone,
                SupportFormFields.Country, SupportFormFields.City, SupportFormFields.Email,
            ],
            fields.Keys);
        Assert.DoesNotContain(SupportFormFields.BusinessName, fields.Keys);
        Assert.True(fields[SupportFormFields.DocumentType]);
        Assert.True(fields[SupportFormFields.DocumentNumber]);
        Assert.True(fields[SupportFormFields.LastNames]);
        Assert.False(fields[SupportFormFields.Address]);
        Assert.False(fields[SupportFormFields.LandlinePhone]);
    }

    [Fact]
    public void NitSwapsNamesForBusinessNameWithTheTenantRequirement()
    {
        var required = Fields(SupportFormVariant.Nit, Form(businessNameRequired: true));
        var optional = Fields(SupportFormVariant.Nit, Form(businessNameRequired: false));

        Assert.DoesNotContain(SupportFormFields.FirstNames, required.Keys);
        Assert.DoesNotContain(SupportFormFields.LastNames, required.Keys);
        Assert.True(required[SupportFormFields.BusinessName]);
        Assert.False(optional[SupportFormFields.BusinessName]);
        Assert.True(optional[SupportFormFields.DocumentType]);
        Assert.True(optional[SupportFormFields.Email]);
    }

    [Fact]
    public void HiddenAddressAndLandlineAreNotListed()
    {
        var form = Form(showAddress: false, showLandline: false);

        foreach (var variant in Enum.GetValues<SupportFormVariant>())
        {
            var keys = Fields(variant, form).Keys;
            Assert.DoesNotContain(SupportFormFields.Address, keys);
            Assert.DoesNotContain(SupportFormFields.LandlinePhone, keys);
        }
    }

    [Fact]
    public void NoDocumentAsksNamesMobileCountryCityAndEmail()
    {
        var fields = Fields(SupportFormVariant.NoDocument, Form(showDocument: false));

        Assert.Equal(
            [
                SupportFormFields.FirstNames, SupportFormFields.LastNames, SupportFormFields.MobilePhone, SupportFormFields.Country,
                SupportFormFields.City, SupportFormFields.Email,
            ],
            fields.Keys);
        Assert.All(fields.Values, Assert.True);
    }

    [Fact]
    public void AnonymousAsksNothing() => Assert.Empty(SupportFormFields.For(SupportFormVariant.Anonymous, Form()));

    [Fact]
    public void TheFixedFieldsAreAlwaysRequired()
    {
        var lenient = Form(showAddress: false, showLandline: false, businessNameRequired: false);

        foreach (var variant in new[] { SupportFormVariant.Natural, SupportFormVariant.Nit, SupportFormVariant.NoDocument })
        {
            var fields = Fields(variant, lenient);
            Assert.True(fields[SupportFormFields.MobilePhone]);
            Assert.True(fields[SupportFormFields.Country]);
            Assert.True(fields[SupportFormFields.City]);
            Assert.True(fields[SupportFormFields.Email]);
        }

        Assert.True(Fields(SupportFormVariant.Natural, lenient)[SupportFormFields.FirstNames]);
    }

    [Theory]
    [InlineData(true, true, "CC", SupportFormVariant.Anonymous)]
    [InlineData(false, false, "NIT", SupportFormVariant.NoDocument)]
    [InlineData(false, true, " nit ", SupportFormVariant.Nit)]
    [InlineData(false, true, "CC", SupportFormVariant.Natural)]
    [InlineData(false, true, null, SupportFormVariant.Natural)]
    public void TheVariantFollowsAnonymityTheDocumentPairAndTheType(
        bool anonymous, bool showDocument, string? documentType, SupportFormVariant expected) =>
        Assert.Equal(expected, SupportFormFields.VariantOf(anonymous, showDocument, documentType));

    [Fact]
    public void TheDefaultFormShowsEverythingAndIsUnpublished()
    {
        var form = SupportFormVersion.Default(TenantId);

        Assert.Equal(0, form.Version);
        Assert.False(form.IsPublished);
        Assert.True(form.ShowDocument);
        Assert.True(form.ShowAddress);
        Assert.True(form.ShowLandline);
        Assert.True(form.BusinessNameRequired);
        Assert.Null(form.AccentColor);
        // Spec §9.3: los adjuntos los prende el tenant; mientras no guarde, el formulario no los ofrece.
        Assert.False(form.AllowAttachments);
    }

    [Fact]
    public void AVersionKeepsWhetherItAllowsAttachments()
    {
        Assert.True(Create(allowAttachments: true).AllowAttachments);
        Assert.False(Create(allowAttachments: false).AllowAttachments);
    }

    [Theory]
    [InlineData("#12abEF", true)]
    [InlineData("12abef", false)]
    [InlineData("#12abe", false)]
    [InlineData("#12abeg", false)]
    public void TheAccentColorIsHashAndSixHex(string color, bool valid)
    {
        if (valid)
        {
            Assert.Equal(color, Create(accentColor: color).AccentColor);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => Create(accentColor: color));
        }
    }

    [Fact]
    public void IntroAndSuccessHaveLimitsAndTheVersionStartsAtOne()
    {
        Assert.Throws<ArgumentException>(() => Create(introText: new string('a', SupportFormVersion.IntroTextMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => Create(successMessage: new string('a', SupportFormVersion.SuccessMessageMaxLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(version: 0));
        Assert.Null(Create(introText: "   ").IntroText);
    }

    private static SupportFormVersion Create(
        int version = 1,
        string? accentColor = null,
        string? introText = null,
        string? successMessage = null,
        bool allowAttachments = false) =>
        SupportFormVersion.Create(
            Guid.CreateVersion7(), TenantId, version, false, true, true, true, true, allowAttachments, accentColor, introText,
            successMessage, Guid.CreateVersion7(), Now);
}
