using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.2: <c>clientId</c> GUID no vacío, <c>replyTo</c> no vacío si viene, <c>file</c> de
/// 1 B a 16 MB y OGG/Opus por contenido. Las claves de <c>errors</c> son las del form: <c>clientId</c>, <c>file</c>,
/// <c>replyTo</c>.</summary>
public sealed class SendAudioMessageValidatorTests
{
    private const string SizeMessage = "Graba una nota de voz de hasta 16 MB.";
    private const string FormatMessage = "La nota de voz no está en el formato que admite WhatsApp. Graba de nuevo.";

    private static readonly Guid ClientId = Guid.CreateVersion7();

    private static SendAudioMessageCommand Command(byte[]? audio, Guid? clientId, Guid? replyTo = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), clientId, audio, replyTo);

    [Fact]
    public void AVoiceNoteWithItsClientIdIsValid() =>
        Assert.True(new SendAudioMessageValidator().Validate(Command(OggOpusFixture.VoiceNote(), ClientId)).IsValid);

    [Fact]
    public void AMissingOrEmptyClientIdFailsOnClientId()
    {
        var validator = new SendAudioMessageValidator();

        Assert.Equal(["clientId"], validator.Validate(Command(OggOpusFixture.VoiceNote(), null)).Errors.Select(failure => failure.PropertyName));
        Assert.Equal(["clientId"], validator.Validate(Command(OggOpusFixture.VoiceNote(), Guid.Empty)).Errors.Select(failure => failure.PropertyName));
    }

    [Fact]
    public void AMissingOrEmptyFileFailsOnFileWithTheSizeMessage()
    {
        var validator = new SendAudioMessageValidator();

        foreach (var audio in new[] { null, Array.Empty<byte>() })
        {
            var failure = Assert.Single(validator.Validate(Command(audio, ClientId)).Errors);
            Assert.Equal(("file", SizeMessage), (failure.PropertyName, failure.ErrorMessage));
        }
    }

    [Fact]
    public void SixteenMegabytesAreValidAndOneByteMoreIsNot()
    {
        var validator = new SendAudioMessageValidator();
        var limit = new byte[SendAudioMessageValidator.MaxAudioBytes];
        OggOpusFixture.VoiceNote().CopyTo(limit, 0);
        var over = new byte[SendAudioMessageValidator.MaxAudioBytes + 1];
        OggOpusFixture.VoiceNote().CopyTo(over, 0);

        Assert.True(validator.Validate(Command(limit, ClientId)).IsValid);
        var failure = Assert.Single(validator.Validate(Command(over, ClientId)).Errors);
        Assert.Equal(("file", SizeMessage), (failure.PropertyName, failure.ErrorMessage));
    }

    [Fact]
    public void AWebmAnMp4OrAnOggVorbisFailsOnFileWithTheFormatMessage()
    {
        var validator = new SendAudioMessageValidator();

        foreach (var audio in new[] { OggOpusFixture.WebmHeader(), OggOpusFixture.Mp4Header(), OggOpusFixture.VorbisFirstPage() })
        {
            var failure = Assert.Single(validator.Validate(Command(audio, ClientId)).Errors);
            Assert.Equal(("file", FormatMessage), (failure.PropertyName, failure.ErrorMessage));
        }
    }

    [Fact]
    public void AnEmptyReplyToFailsOnReplyToAndAMissingOneIsValid()
    {
        var validator = new SendAudioMessageValidator();

        Assert.Equal(["replyTo"], validator.Validate(Command(OggOpusFixture.VoiceNote(), ClientId, Guid.Empty)).Errors.Select(failure => failure.PropertyName));
        Assert.True(validator.Validate(Command(OggOpusFixture.VoiceNote(), ClientId, Guid.CreateVersion7())).IsValid);
    }
}
