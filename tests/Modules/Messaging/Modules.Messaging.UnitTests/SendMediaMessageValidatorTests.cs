using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Messaging-media-reply R11, R17 y R18: forma del cuerpo de <c>POST .../messages/media</c>. Las claves de
/// <c>errors</c> son las del JSON: <c>clientId</c>, <c>uploadId</c>, <c>fileName</c>, <c>caption</c>, <c>replyTo</c>. Qué tipo es
/// y cuánto pesa lo decide el handler con códigos de dominio.</summary>
public sealed class SendMediaMessageValidatorTests
{
    private static SendMediaMessageCommand Command(
        Guid? clientId = null, Guid? uploadId = null, string? fileName = "foto.jpg", string? caption = null, Guid? replyTo = null, bool noIds = false) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), noIds ? clientId : clientId ?? Guid.CreateVersion7(), noIds ? uploadId : uploadId ?? Guid.CreateVersion7(), fileName, caption, replyTo);

    private static IEnumerable<string> KeysOf(SendMediaMessageCommand command) =>
        new SendMediaMessageValidator().Validate(command).Errors.Select(failure => failure.PropertyName).Order(StringComparer.Ordinal);

    [Fact]
    public void ABodyWithItsIdsAndFileNameIsValidWithOrWithoutCaptionAndReply()
    {
        var validator = new SendMediaMessageValidator();

        Assert.True(validator.Validate(Command()).IsValid);
        Assert.True(validator.Validate(Command(caption: "Mira esto", replyTo: Guid.CreateVersion7())).IsValid);
    }

    // S23: sin clientId y con fileName vacío, el mapa trae esas dos claves (y la del uploadId si también falta).
    [Fact]
    public void AMissingClientIdAndAnEmptyFileNameFailOnTheirOwnKeys()
    {
        Assert.Equal(["clientId", "fileName"], KeysOf(Command(uploadId: Guid.CreateVersion7(), fileName: "", noIds: true)));
        Assert.Equal(["clientId", "fileName", "uploadId"], KeysOf(Command(fileName: null, noIds: true)));
    }

    [Fact]
    public void AnEmptyGuidIsAsBadAsAMissingOne()
    {
        Assert.Equal(["clientId"], KeysOf(Command(clientId: Guid.Empty)));
        Assert.Equal(["uploadId"], KeysOf(Command(uploadId: Guid.Empty)));
        Assert.Equal(["replyTo"], KeysOf(Command(replyTo: Guid.Empty)));
    }

    // S22: 1024 pasa; 1025 falla en caption.
    [Fact]
    public void ACaptionOf1024CharactersPassesAndOneOf1025FailsOnCaption()
    {
        Assert.True(new SendMediaMessageValidator().Validate(Command(caption: new string('a', 1024))).IsValid);

        var failure = Assert.Single(new SendMediaMessageValidator().Validate(Command(caption: new string('a', 1025))).Errors);
        Assert.Equal("caption", failure.PropertyName);
    }

    // message_media.file_name es varchar(256).
    [Fact]
    public void AFileNameLongerThanTheColumnFailsOnFileName() =>
        Assert.Equal(["fileName"], KeysOf(Command(fileName: new string('a', 253) + ".jpg")));

    // Spec 2026-10-20 D7: WhatsApp no admite texto en audio y en Instagram no viaja: un audio con texto es 422 en caption.
    [Theory]
    [InlineData("a.mp3")]
    [InlineData("a.m4a")]
    [InlineData("a.AAC")]
    [InlineData("a.amr")]
    [InlineData("a.ogg")]
    public void AnAudioWithACaptionFailsOnCaption(string fileName)
    {
        Assert.Equal(["caption"], KeysOf(Command(fileName: fileName, caption: "Escúchalo")));
        Assert.True(new SendMediaMessageValidator().Validate(Command(fileName: fileName)).IsValid);
        Assert.True(new SendMediaMessageValidator().Validate(Command(fileName: fileName, caption: "  ")).IsValid);
    }

    [Theory]
    [InlineData("a.mp4")]
    [InlineData("a.3gp")]
    public void AVideoAcceptsACaption(string fileName) =>
        Assert.True(new SendMediaMessageValidator().Validate(Command(fileName: fileName, caption: "Mira el producto")).IsValid);
}
