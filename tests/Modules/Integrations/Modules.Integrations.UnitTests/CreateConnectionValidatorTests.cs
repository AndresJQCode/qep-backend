using FluentValidation.Results;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Códigos de error»: <c>validation.failed</c> con <c>errors</c> por
/// <c>name</c>, <c>providerKey</c>, <c>fields.&lt;key&gt;</c> y <c>secrets.&lt;key&gt;</c>, en minúscula
/// y exactos (P26): es lo único que el formulario sabe leer para marcar el input.
/// </summary>
public sealed class CreateConnectionValidatorTests
{
    private static CreateConnectionCommand Command(
        string? name = "WhatsApp sede norte",
        string? providerKey = "zenvia",
        Dictionary<string, string?>? fields = null,
        Dictionary<string, string?>? secrets = null) =>
        new(
            Guid.CreateVersion7(),
            providerKey,
            name,
            fields ?? new Dictionary<string, string?>
            {
                [ZenviaFieldKeys.FromNumber] = "573001234567",
                [ZenviaFieldKeys.TemplateId] = TemplateId,
            },
            secrets ?? new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "zenvia-token-TEST-1" });

    private const string TemplateId = "d0e79149-ed61-4784-9883-d86807f46ce5";

    private const string TemplateIdMessage =
        "Copia el ID de la plantilla tal como aparece en Zenvia (formato xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).";

    private static async Task<List<ValidationFailure>> ErrorsAsync(CreateConnectionCommand command) =>
        (await new CreateConnectionValidator(new IntegrationProviderCatalog())
            .ValidateAsync(command, TestContext.Current.CancellationToken)).Errors;

    [Fact]
    public async Task AValidZenviaBodyPasses() =>
        Assert.Empty(await ErrorsAsync(Command()));

    [Theory]
    [InlineData(null)]
    [InlineData("whatsapp")]
    [InlineData("Zenvia")]
    public async Task AnUnknownProviderGoesToProviderKey(string? providerKey)
    {
        var error = Assert.Single(await ErrorsAsync(Command(providerKey: providerKey)));

        Assert.Equal("providerKey", error.PropertyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\u0000b")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task AnInvalidNameGoesToName(string? name)
    {
        var error = Assert.Single(await ErrorsAsync(Command(name: name)));

        Assert.Equal("name", error.PropertyName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingSecretIsRequiredOnCreate(string? token)
    {
        var error = Assert.Single(await ErrorsAsync(Command(
            secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = token })));

        Assert.Equal("secrets.apiToken", error.PropertyName);
        Assert.Equal("Completa este campo.", error.ErrorMessage);
    }

    // Review Focus 2: un secreto dentro de fields terminaría en jsonb, en claro.
    [Fact]
    public async Task ASecretSentAsAFieldIsUnknownAndNamedByItsKey()
    {
        var errors = await ErrorsAsync(Command(fields: new Dictionary<string, string?>
        {
            [ZenviaFieldKeys.FromNumber] = "573001234567",
            [ZenviaFieldKeys.TemplateId] = TemplateId,
            [ZenviaFieldKeys.ApiToken] = "zenvia-token-TEST-1",
        }));

        var error = Assert.Single(errors);
        Assert.Equal("fields.apiToken", error.PropertyName);
        Assert.Equal("Este campo no existe para este proveedor.", error.ErrorMessage);
    }

    [Fact]
    public async Task AKeyThatIsNotAFieldKeyIsReportedWithoutEchoingIt()
    {
        var errors = await ErrorsAsync(Command(fields: new Dictionary<string, string?>
        {
            [ZenviaFieldKeys.FromNumber] = "573001234567",
            [ZenviaFieldKeys.TemplateId] = TemplateId,
            ["a.b<script>"] = "x",
        }));

        var error = Assert.Single(errors);
        Assert.Equal("fields", error.PropertyName);
        Assert.DoesNotContain("script", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("+573001234567", "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15).")]
    [InlineData("57300\u00001234567", "Escribe el número con indicativo de país, sólo dígitos y sin '+' (entre 10 y 15).")]
    [InlineData("5730012345678901", "Usa máximo 15 caracteres.")]
    public async Task AFromNumberWithoutItsShapeGoesToItsField(string fromNumber, string message)
    {
        var error = Assert.Single(await ErrorsAsync(Command(
            fields: new Dictionary<string, string?>
            {
                [ZenviaFieldKeys.FromNumber] = fromNumber,
                [ZenviaFieldKeys.TemplateId] = TemplateId,
            })));

        Assert.Equal("fields.fromNumber", error.PropertyName);
        Assert.Equal(message, error.ErrorMessage);
    }

    [Fact]
    public async Task NullFieldsAndSecretsAreRequiredNotAServerError()
    {
        var errors = await new CreateConnectionValidator(new IntegrationProviderCatalog()).ValidateAsync(
            new CreateConnectionCommand(Guid.CreateVersion7(), "zenvia", "Norte", null, null),
            TestContext.Current.CancellationToken);

        Assert.Equal(["fields.fromNumber", "fields.templateId", "secrets.apiToken"], errors.Errors.Select(error => error.PropertyName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ZenviaWithoutTemplateIdFails()
    {
        var error = Assert.Single(await ErrorsAsync(Command(
            fields: new Dictionary<string, string?> { [ZenviaFieldKeys.FromNumber] = "573001234567" })));

        Assert.Equal("fields.templateId", error.PropertyName);
    }

    [Theory]
    [InlineData(ZenviaFieldKeys.TemplateId, "plantilla-1")]
    [InlineData(ZenviaFieldKeys.TemplateId, "d0e79149ed6147849883d86807f46ce5")]
    [InlineData(ZenviaFieldKeys.OrderTemplateId, "x")]
    public async Task ZenviaTemplateIdMustBeAUuid(string key, string value)
    {
        var fields = new Dictionary<string, string?>
        {
            [ZenviaFieldKeys.FromNumber] = "573001234567",
            [ZenviaFieldKeys.TemplateId] = TemplateId,
            [key] = value,
        };

        var error = Assert.Single(await ErrorsAsync(Command(fields: fields)));

        Assert.Equal($"fields.{key}", error.PropertyName);
        Assert.Equal(TemplateIdMessage, error.ErrorMessage);
    }

    [Fact]
    public async Task ZenviaWithOrderTemplateIdPasses() =>
        Assert.Empty(await ErrorsAsync(Command(fields: new Dictionary<string, string?>
        {
            [ZenviaFieldKeys.FromNumber] = "573001234567",
            [ZenviaFieldKeys.TemplateId] = TemplateId,
            [ZenviaFieldKeys.OrderTemplateId] = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d",
        })));

    [Fact]
    public async Task NoMessageCarriesTheValueThatWasSent()
    {
        var errors = await ErrorsAsync(Command(
            fields: new Dictionary<string, string?>
            {
                [ZenviaFieldKeys.FromNumber] = "+57SENTINEL",
                [ZenviaFieldKeys.TemplateId] = TemplateId,
            },
            secrets: new Dictionary<string, string?> { [ZenviaFieldKeys.ApiToken] = "con espacio SENTINEL" }));

        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.DoesNotContain("SENTINEL", error.ErrorMessage, StringComparison.Ordinal));
    }

    // Spec §6.1: el formulario genérico no puede crear una conexión de Meta sin token.
    [Fact]
    public async Task AMetaSignupProviderIsRejectedOnTheProviderKey()
    {
        var errors = await ErrorsAsync(new CreateConnectionCommand(
            Guid.CreateVersion7(), "whatsapp-cloud", "Ventas",
            new Dictionary<string, string?>(), new Dictionary<string, string?>()));

        var error = Assert.Single(errors);
        Assert.Equal("providerKey", error.PropertyName);
        Assert.Contains("flujo de Meta", error.ErrorMessage, StringComparison.Ordinal);
    }
}
