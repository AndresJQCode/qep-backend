using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-11 §6.2 y §10: el formato se decide por contenido, nunca por el <c>Content-Type</c> del
/// navegador. Un WebM (Chrome sin codificador) o un MP4 (Safari) se rechazan antes de llegar a Meta.</summary>
public sealed class OggOpusSnifferTests
{
    [Fact]
    public void AVoiceNoteRecordedAsOggOpusIsAccepted() => Assert.True(OggOpusSniffer.IsOggOpus(OggOpusFixture.VoiceNote()));

    [Fact]
    public void TheFirstPageAloneIsEnough() => Assert.True(OggOpusSniffer.IsOggOpus(OggOpusFixture.FirstPage()));

    [Fact]
    public void AnOggWithVorbisIsRejected() => Assert.False(OggOpusSniffer.IsOggOpus(OggOpusFixture.VorbisFirstPage()));

    [Fact]
    public void AWebmFromChromeIsRejected() => Assert.False(OggOpusSniffer.IsOggOpus(OggOpusFixture.WebmHeader()));

    [Fact]
    public void AnMp4FromSafariIsRejected() => Assert.False(OggOpusSniffer.IsOggOpus(OggOpusFixture.Mp4Header()));

    [Fact]
    public void AnEmptyFileIsRejected() => Assert.False(OggOpusSniffer.IsOggOpus(ReadOnlySpan<byte>.Empty));

    [Theory]
    [InlineData(4)] // sólo «OggS»
    [InlineData(26)] // la cabecera sin el número de segmentos
    [InlineData(28)] // cabecera y tabla de segmentos, sin el paquete
    [InlineData(34)] // «OpusHe»: el paquete cortado
    public void LessThanAPageIsRejected(int length) =>
        Assert.False(OggOpusSniffer.IsOggOpus(OggOpusFixture.FirstPage().AsSpan(0, length)));

    [Fact]
    public void TheMimeTypeIsTheOneMetaTreatsAsVoice() => Assert.Equal("audio/ogg", OggOpusSniffer.MimeType);
}
