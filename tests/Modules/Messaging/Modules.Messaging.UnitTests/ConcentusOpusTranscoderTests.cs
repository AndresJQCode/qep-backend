using System.Buffers.Binary;
using System.Text;
using Modules.Messaging.Infrastructure.Media;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec 2026-10-14 decisión 16: la nota OGG/Opus pasa a WAV PCM 16 bits mono a 16 kHz; lo que no es Opus falla claro.</summary>
public sealed class ConcentusOpusTranscoderTests
{
    private readonly ConcentusOpusTranscoder _transcoder = new();

    [Fact]
    public void AVoiceNoteBecomesACanonicalWavAt16KhzMono()
    {
        var wav = _transcoder.ToWav(OggOpusFixture.VoiceNote());

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20))); // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22))); // mono
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        Assert.Equal(wav.Length - 44, dataLength);
        // 20 ms de silencio a 16 kHz son 320 muestras de 2 bytes.
        Assert.Equal(640, dataLength);
        Assert.Equal(36 + dataLength, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
    }

    [Theory]
    [InlineData("vorbis")]
    [InlineData("webm")]
    [InlineData("empty")]
    public void WhatIsNotOpusFailsAsInvalidData(string kind)
    {
        var bytes = kind switch
        {
            "vorbis" => OggOpusFixture.VorbisFirstPage(),
            "webm" => OggOpusFixture.WebmHeader(),
            _ => [],
        };

        Assert.Throws<InvalidDataException>(() => _transcoder.ToWav(bytes));
    }
}
