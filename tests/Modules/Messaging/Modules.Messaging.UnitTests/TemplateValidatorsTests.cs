using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §5.2: cada valor de <c>parameters</c> de 1 a 1024 caracteres, sin saltos de línea ni tabulaciones y sin
/// más de 4 espacios seguidos (la regla 132018 de Meta); el 422 lleva la clave exacta <c>parameters.&lt;clave&gt;</c>. Las demás
/// claves de <c>errors</c> son las del cuerpo JSON.</summary>
public sealed class TemplateValidatorsTests
{
    private static readonly Guid ClientId = Guid.CreateVersion7();

    private static SendTemplateMessageCommand Send(
        Guid? clientId, string? name, string? language, IReadOnlyDictionary<string, string?>? parameters) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), clientId, name, language, parameters);

    private static StartConversationCommand Start(
        Guid? clientId, Guid? connectionId, Guid? customerId, string? name, string? language, IReadOnlyDictionary<string, string?>? parameters) =>
        new(Guid.CreateVersion7(), clientId, connectionId, customerId, name, language, parameters);

    private static Dictionary<string, string?> Params(string key, string? value) => new Dictionary<string, string?> { [key] = value };

    private static string[] Keys(FluentValidation.Results.ValidationResult result) => result.Errors.Select(failure => failure.PropertyName).Distinct().Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void AValidSendPasses() =>
        Assert.True(new SendTemplateMessageValidator().Validate(Send(ClientId, "hola", "es", Params("first_name", "Ana María"))).IsValid);

    [Fact]
    public void ATemplateWithoutParametersPasses() =>
        Assert.True(new SendTemplateMessageValidator().Validate(Send(ClientId, "fijo", "es", new Dictionary<string, string?>())).IsValid);

    [Fact]
    public void AMissingOrEmptyClientIdNameLanguageOrParametersFailOnTheirOwnKey()
    {
        var validator = new SendTemplateMessageValidator();

        Assert.Equal(["clientId"], Keys(validator.Validate(Send(null, "hola", "es", Params("1", "a")))));
        Assert.Equal(["clientId"], Keys(validator.Validate(Send(Guid.Empty, "hola", "es", Params("1", "a")))));
        Assert.Equal(["name"], Keys(validator.Validate(Send(ClientId, " ", "es", Params("1", "a")))));
        Assert.Equal(["language"], Keys(validator.Validate(Send(ClientId, "hola", null, Params("1", "a")))));
        Assert.Equal(["parameters"], Keys(validator.Validate(Send(ClientId, "hola", "es", null))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("línea 1\nlínea 2")]
    [InlineData("línea 1\r\nlínea 2")]
    [InlineData("col 1\tcol 2")]
    [InlineData("demasiado     espacio")]
    public void AValueThatBreaksMetasRulesFailsOnItsParameterKey(string? value) =>
        Assert.Equal(["parameters.first_name"], Keys(new SendTemplateMessageValidator().Validate(Send(ClientId, "hola", "es", Params("first_name", value)))));

    [Fact]
    public void FourConsecutiveSpacesAndExactly1024CharactersAreValidAndOneMoreIsNot()
    {
        var validator = new SendTemplateMessageValidator();

        Assert.True(validator.Validate(Send(ClientId, "hola", "es", Params("1", "cuatro    espacios"))).IsValid);
        Assert.True(validator.Validate(Send(ClientId, "hola", "es", Params("1", new string('x', 1024)))).IsValid);
        Assert.Equal(["parameters.1"], Keys(validator.Validate(Send(ClientId, "hola", "es", Params("1", new string('x', 1025))))));
    }

    [Fact]
    public void EveryBadValueIsReportedByItsOwnKey()
    {
        var parameters = new Dictionary<string, string?> { ["a"] = "bien", ["b"] = "mal\n", ["c"] = string.Empty };

        Assert.Equal(["parameters.b", "parameters.c"], Keys(new SendTemplateMessageValidator().Validate(Send(ClientId, "hola", "es", parameters))));
    }

    [Fact]
    public void AValidStartPasses() =>
        Assert.True(new StartConversationValidator().Validate(Start(ClientId, Guid.CreateVersion7(), Guid.CreateVersion7(), "hola", "es", Params("1", "a"))).IsValid);

    [Fact]
    public void AStartFailsOnTheKeysOfItsBody()
    {
        var validator = new StartConversationValidator();
        var connection = Guid.CreateVersion7();
        var customer = Guid.CreateVersion7();

        Assert.Equal(["clientId"], Keys(validator.Validate(Start(null, connection, customer, "hola", "es", Params("1", "a")))));
        Assert.Equal(["connectionId"], Keys(validator.Validate(Start(ClientId, Guid.Empty, customer, "hola", "es", Params("1", "a")))));
        Assert.Equal(["customerId"], Keys(validator.Validate(Start(ClientId, connection, null, "hola", "es", Params("1", "a")))));
        Assert.Equal(["template.name"], Keys(validator.Validate(Start(ClientId, connection, customer, "", "es", Params("1", "a")))));
        Assert.Equal(["template.language"], Keys(validator.Validate(Start(ClientId, connection, customer, "hola", null, Params("1", "a")))));
        Assert.Equal(["parameters.1"], Keys(validator.Validate(Start(ClientId, connection, customer, "hola", "es", Params("1", "a\tb")))));
        Assert.Equal(["parameters"], Keys(validator.Validate(Start(ClientId, connection, customer, "hola", "es", null))));
    }
}
