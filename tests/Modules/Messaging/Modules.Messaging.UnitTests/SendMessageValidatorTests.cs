using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-09 §6.4: <c>text</c> no vacío tras <c>Trim</c>, ≤ 4096, sin <c>\0</c> ni otros
/// caracteres de control (salto de línea, retorno y tabulador sí); <c>clientId</c> GUID no vacío. Las claves de
/// <c>errors</c> son las del cuerpo JSON.</summary>
public sealed class SendMessageValidatorTests
{
    private static readonly Guid ClientId = Guid.CreateVersion7();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("hola\u0000")]
    [InlineData("ho\u001Bla")]
    [InlineData("ho\u007Fla")]
    public void AnInvalidTextFailsOnText(string? text)
    {
        var result = new SendMessageValidator().Validate(new SendMessageCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), ClientId, text));

        Assert.Equal(["text"], result.Errors.Select(failure => failure.PropertyName));
    }

    [Fact]
    public void FourThousandNinetySixCharactersAfterTrimAreValidAndOneMoreIsNot()
    {
        var validator = new SendMessageValidator();

        Assert.True(validator.Validate(new SendMessageCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), ClientId, "  " + new string('x', 4096) + "  ")).IsValid);
        Assert.False(validator.Validate(new SendMessageCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), ClientId, new string('x', 4097))).IsValid);
    }

    [Fact]
    public void LineBreaksAndTabsAreValid() =>
        Assert.True(new SendMessageValidator().Validate(new SendMessageCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), ClientId, "Hola:\r\n\t- 12")).IsValid);

    [Fact]
    public void AMissingOrEmptyClientIdFailsOnClientId()
    {
        var validator = new SendMessageValidator();

        Assert.Equal(["clientId"], validator.Validate(new SendMessageCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), null, "x")).Errors.Select(failure => failure.PropertyName).Distinct());
        Assert.Equal(["clientId"], validator.Validate(new SendMessageCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.Empty, "x")).Errors.Select(failure => failure.PropertyName).Distinct());
    }
}
