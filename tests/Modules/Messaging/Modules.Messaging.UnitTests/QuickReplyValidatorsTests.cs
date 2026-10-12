using FluentValidation.Results;
using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-12 §3.3: las reglas de formato del agregado, repetidas en el validador para que el 422
/// <c>validation.failed</c> traiga <c>errors.shortcut</c> y <c>errors.body</c>. Los mensajes llevan llaves literales
/// (<c>{nombre}</c>): se agregan sin pasar por el formateador de FluentValidation, que las trataría como placeholders.</summary>
public sealed class QuickReplyValidatorsTests
{
    private const string ShortcutMessage = "El atajo lleva de 2 a 32 letras minúsculas, números o guiones, sin «/» ni espacios.";
    private const string BodyMessage = "Escribe una respuesta de 1 a 4096 caracteres.";

    private sealed record Input(string? Shortcut, string? Body) : IQuickReplyInput;

    private sealed class InputValidator : QuickReplyInputValidator<Input>;

    private static ValidationResult Validate(string? shortcut, string? body) => new InputValidator().Validate(new Input(shortcut, body));

    private static string[] Keys(ValidationResult result) =>
        result.Errors.Select(failure => failure.PropertyName).Order(StringComparer.Ordinal).ToArray();

    [Theory]
    [InlineData("horarios")]
    [InlineData(" Horarios ")]
    [InlineData("pago-2")]
    public void AValidReplyPasses(string shortcut) =>
        Assert.True(Validate(shortcut, "Hola {nombre}, soy {asesor} de {empresa}.").IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("/horarios")]
    [InlineData("hola mundo")]
    [InlineData("hola_mundo")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
    public void AnInvalidShortcutFailsOnShortcut(string? shortcut)
    {
        var failure = Assert.Single(Validate(shortcut, "Hola").Errors);
        Assert.Equal(("shortcut", ShortcutMessage), (failure.PropertyName, failure.ErrorMessage));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("\n\t ")]
    public void AnEmptyBodyFailsOnBody(string? body)
    {
        var failure = Assert.Single(Validate("horarios", body).Errors);
        Assert.Equal(("body", BodyMessage), (failure.PropertyName, failure.ErrorMessage));
    }

    [Fact]
    public void FourThousandNinetySixCharactersAfterTrimPassAndOneMoreDoesNot()
    {
        Assert.True(Validate("largo", "  " + new string('a', 4096) + "  ").IsValid);
        var failure = Assert.Single(Validate("largo", new string('a', 4097)).Errors);
        Assert.Equal(("body", BodyMessage), (failure.PropertyName, failure.ErrorMessage));
    }

    // Review Focus 3: el mensaje nombra la variable y muestra las tres permitidas con sus llaves, sin que el formateador las coma.
    [Theory]
    [InlineData("Hola {Nombre}", "{Nombre}")]
    [InlineData("Hola { nombre }", "{ nombre }")]
    [InlineData("Hola {}", "{}")]
    [InlineData("Total {precio} para {nombre}", "{precio}")]
    public void AnUnknownVariableFailsOnBodyNamingIt(string body, string variable)
    {
        var failure = Assert.Single(Validate("saludo", body).Errors);
        Assert.Equal(("body", $"«{variable}» no es una variable. Usa {{nombre}}, {{asesor}} o {{empresa}}."), (failure.PropertyName, failure.ErrorMessage));
    }

    [Theory]
    [InlineData("Precio {10% off")]
    [InlineData("Cierra } antes")]
    [InlineData("{{nombre}}")]
    [InlineData("{nombre}{nombre}")]
    public void LooseBracesAreText(string body) => Assert.True(Validate("saludo", body).IsValid);

    [Fact]
    public void BothFieldsFailTogether() => Assert.Equal(["body", "shortcut"], Keys(Validate("/horarios", "   ")));

    [Fact]
    public void TheCreateValidatorUsesTheSharedRules() =>
        Assert.Equal(["body", "shortcut"], Keys(new CreateQuickReplyValidator().Validate(new CreateQuickReplyCommand(Guid.CreateVersion7(), "/x", ""))));

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void UpdateAndDeleteNeedAPositiveVersion(long? version)
    {
        var update = Assert.Single(new UpdateQuickReplyValidator().Validate(
            new UpdateQuickReplyCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "horarios", "Hola", version)).Errors);
        var delete = Assert.Single(new DeleteQuickReplyValidator().Validate(
            new DeleteQuickReplyCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), version)).Errors);

        Assert.Equal(("version", "Falta la versión de la respuesta que estás viendo."), (update.PropertyName, update.ErrorMessage));
        Assert.Equal(("version", "Falta la versión de la respuesta que estás viendo."), (delete.PropertyName, delete.ErrorMessage));
    }

    [Fact]
    public void TheUpdateValidatorUsesTheSharedRules() =>
        Assert.Equal(["body", "shortcut"], Keys(new UpdateQuickReplyValidator().Validate(
            new UpdateQuickReplyCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "a", "Hola {cliente}", 1))));
}
