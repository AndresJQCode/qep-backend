using FluentValidation;
using Modules.Quotations.Application;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-10-07, «Validador»: sólo formato y sólo de lo que viene. Lo requerido en Own depende
/// de la fila y lo decide el handler (B8); si lo exigiera el validador, <c>{ "mode": "Own" }</c>
/// —volver a la cuenta propia ya guardada— sería siempre 422. En Shared y Disabled los campos de
/// la cuenta propia se ignoran. Ningún mensaje lleva el valor de la key.
/// </summary>
public sealed class UpdateWhatsAppSettingsValidatorTests
{
    private const string ValidTemplate = "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f";
    private static readonly UpdateWhatsAppSettingsValidator Validator = new();

    private static UpdateWhatsAppSettingsCommand Command(
        string mode = "Own",
        string? provider = null,
        string? apiKey = null,
        string? fromNumber = null,
        string? templateId = null,
        long expectedVersion = 1) =>
        new(Guid.CreateVersion7(), mode, provider, apiKey, fromNumber, templateId, expectedVersion);

    // Evita CA1861 (matriz constante por llamada) en cada Assert.Equal.
    private static string[] Only(string property) => [property];

    private static IReadOnlyList<string> FailedProperties(UpdateWhatsAppSettingsCommand command) =>
        [.. Validator.Validate(command).Errors.Select(error => error.PropertyName).Distinct()];

    [Theory]
    [InlineData("Shared")]
    [InlineData("Own")]
    [InlineData("Disabled")]
    public void EachModeByItsExactNameIsValid(string mode) =>
        Assert.Empty(FailedProperties(Command(mode)));

    // Review Focus 2: ordinal. Enum.TryParse con ignoreCase aceptaría "own".
    [Theory]
    [InlineData("own")]
    [InlineData("OWN")]
    [InlineData("")]
    [InlineData("Enabled")]
    [InlineData("1")]
    public void ModeIsOrdinalAndRequired(string mode) =>
        Assert.Equal(Only("Mode"), FailedProperties(Command(mode)));

    // Volver a la cuenta propia guardada: el validador no exige nada (lo decide el handler).
    [Fact]
    public void OwnAloneIsValid() => Assert.Empty(FailedProperties(Command("Own")));

    [Fact]
    public void AFullValidOwnBodyIsValid() =>
        Assert.Empty(FailedProperties(Command(
            "Own", "Zenvia", "  abc-123_XYZ.!~  ", " 573001234567 ", ValidTemplate)));

    [Theory]
    [InlineData("zenvia")]
    [InlineData("Twilio")]
    [InlineData("")]
    public void TheProviderIsZenviaByItsExactName(string provider) =>
        Assert.Equal(Only("Provider"), FailedProperties(Command("Own", provider: provider)));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("con espacio")]
    [InlineData("tab\tdentro")]
    [InlineData("control\u0001")]
    [InlineData("contraseña")]
    public void TheApiKeyIsVisibleAsciiWithoutSpaces(string apiKey) =>
        Assert.Equal(Only("ApiKey"), FailedProperties(Command("Own", apiKey: apiKey)));

    [Fact]
    public void TheApiKeyHasAtMost512Characters()
    {
        Assert.Empty(FailedProperties(Command("Own", apiKey: new string('a', 512))));
        Assert.Equal(Only("ApiKey"), FailedProperties(Command("Own", apiKey: new string('a', 513))));
    }

    [Theory]
    [InlineData("+573001234567")]
    [InlineData("300123456")]
    [InlineData("5730012345678901")]
    [InlineData("57 3001234567")]
    [InlineData("57300123456a")]
    [InlineData("")]
    public void TheFromNumberIsTenToFifteenDigits(string fromNumber) =>
        Assert.Equal(Only("FromNumber"), FailedProperties(Command("Own", fromNumber: fromNumber)));

    [Theory]
    [InlineData("plantilla")]
    [InlineData("{9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f}")]
    [InlineData("9b2f4c1e3d5a4e6b8c7d1a2b3c4d5e6f")]
    [InlineData("")]
    public void TheTemplateIdIsAGuidInFormatD(string templateId) =>
        Assert.Equal(Only("TemplateId"), FailedProperties(Command("Own", templateId: templateId)));

    // Decisión 28: fuera de Own los campos propios ni se validan ni se guardan.
    [Theory]
    [InlineData("Shared")]
    [InlineData("Disabled")]
    public void OutsideOwnTheOwnAccountFieldsAreIgnored(string mode) =>
        Assert.Empty(FailedProperties(Command(
            mode, provider: "Twilio", apiKey: "con espacio", fromNumber: "+57", templateId: "x")));

    [Fact]
    public void TheExpectedVersionIsPositive() =>
        Assert.Equal(Only("ExpectedVersion"), FailedProperties(Command("Shared", expectedVersion: 0)));

    [Fact]
    public void NoErrorMessageContainsTheRejectedKey()
    {
        const string rejected = "SENTINEL con espacio";

        var errors = Validator.Validate(Command("Own", apiKey: rejected)).Errors;

        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.DoesNotContain("SENTINEL", error.ErrorMessage, StringComparison.Ordinal));
        Assert.DoesNotContain("SENTINEL", new ValidationException(errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommandToStringDoesNotContainTheKey()
    {
        var command = Command("Own", "Zenvia", "key-SENTINEL", "573001234567", ValidTemplate);

        Assert.DoesNotContain("key-SENTINEL", command.ToString(), StringComparison.Ordinal);
        Assert.Contains("***", command.ToString(), StringComparison.Ordinal);
    }
}
