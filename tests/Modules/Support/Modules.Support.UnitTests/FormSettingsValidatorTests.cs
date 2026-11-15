using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>Los límites de la configuración del formulario público y de los textos legales (plan fase 3, F14 y F16). El validador da
/// el campo; el dominio repite la regla.</summary>
public sealed class FormSettingsValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static UpdateFormSettingsRequest Form(string? accentColor = null, string? introText = null, string? successMessage = null) =>
        new(true, true, true, true, true, accentColor, introText, successMessage);

    private static List<string> FormErrors(UpdateFormSettingsRequest request, long version = 0) =>
        new UpdateFormSettingsValidator().Validate(UpdateFormSettingsCommand.From(Tenant, request, version))
            .Errors.Select(error => error.PropertyName).ToList();

    private static List<string> LegalErrors(PublishLegalTextsRequest request, long version = 0) =>
        new PublishLegalTextsValidator().Validate(PublishLegalTextsCommand.From(Tenant, request, version))
            .Errors.Select(error => error.PropertyName).ToList();

    [Theory]
    [InlineData("#00B4FF", true)]
    [InlineData("#00b4ff", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("00B4FF", false)]
    [InlineData("#00B4F", false)]
    [InlineData("#00B4FG", false)]
    [InlineData("#00B4FF0", false)]
    public void AccentColorIsHashAndSixHex(string? color, bool valid)
    {
        var errors = FormErrors(Form(accentColor: color));

        if (valid)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.Equal(["AccentColor"], errors);
        }
    }

    [Fact]
    public void IntroIsAtMost1000AndSuccessAtMost500()
    {
        Assert.Empty(FormErrors(Form(introText: new string('a', 1000), successMessage: new string('a', 500))));
        Assert.Equal(
            ["IntroText", "SuccessMessage"],
            FormErrors(Form(introText: new string('a', 1001), successMessage: new string('a', 501))));
    }

    [Fact]
    public void EveryFlagIsRequiredAndTheVersionIsNotNegative()
    {
        var errors = FormErrors(new UpdateFormSettingsRequest(null, null, null, null, null, null, null, null), version: -1);

        Assert.Equal(
            ["IsPublished", "ShowDocument", "ShowAddress", "ShowLandline", "BusinessNameRequired", "ExpectedVersion"], errors);
    }

    [Fact]
    public void LegalTextsLimits()
    {
        var limit = new string('a', 20_000);
        var over = new string('a', 20_001);
        var longUrl = "https://example.com/" + new string('a', 481);

        Assert.Empty(LegalErrors(new PublishLegalTextsRequest(limit, limit, "https://example.com/politica", limit)));
        Assert.Equal(
            ["AuthorizationText", "PolicyText", "PolicyUrl", "TermsText"],
            LegalErrors(new PublishLegalTextsRequest(over, over, longUrl, over)));
        Assert.Equal(["AuthorizationText"], LegalErrors(new PublishLegalTextsRequest("  ", "Política", null, null)));
        Assert.Equal(["PolicyUrl"], LegalErrors(new PublishLegalTextsRequest("Autorizo.", null, "http://example.com/politica", null)));
        Assert.Equal(["ExpectedVersion"], LegalErrors(new PublishLegalTextsRequest("Autorizo.", "Política", null, null), version: -1));
    }
}
