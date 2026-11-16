using Modules.Support.Application;

namespace Modules.Support.UnitTests;

/// <summary>Los límites de la configuración del formulario público (plan fase 3, F14). El validador da el campo; el dominio repite la
/// regla. Los documentos legales tienen su propia clase (<c>PublishLegalDocumentValidatorTests</c>).</summary>
public sealed class FormSettingsValidatorTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();

    private static UpdateFormSettingsRequest Form(string? accentColor = null, string? introText = null, string? successMessage = null) =>
        new(true, true, true, true, true, true, accentColor, introText, successMessage);

    private static List<string> FormErrors(UpdateFormSettingsRequest request, long version = 0) =>
        new UpdateFormSettingsValidator().Validate(UpdateFormSettingsCommand.From(Tenant, request, version))
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

    // Plan L31 (spec 2026-11-15 §9.3): null is «no company»; the empty UUID is a bug of the console.
    [Fact]
    public void AnEmptyOrganizationIdIsAnError() =>
        Assert.Equal(["OrganizationCompanyId"], FormErrors(Form() with { OrganizationCompanyId = Guid.Empty }));

    [Fact]
    public void ANullOrganizationIdIsFine()
    {
        Assert.Empty(FormErrors(Form() with { OrganizationCompanyId = null }));
        Assert.Empty(FormErrors(Form() with { OrganizationCompanyId = Guid.NewGuid() }));
    }

    [Fact]
    public void EveryFlagIsRequiredAndTheVersionIsNotNegative()
    {
        var errors = FormErrors(new UpdateFormSettingsRequest(null, null, null, null, null, null, null, null, null), version: -1);

        Assert.Equal(
            ["IsPublished", "ShowDocument", "ShowAddress", "ShowLandline", "BusinessNameRequired", "AllowAttachments", "ExpectedVersion"],
            errors);
    }
}
