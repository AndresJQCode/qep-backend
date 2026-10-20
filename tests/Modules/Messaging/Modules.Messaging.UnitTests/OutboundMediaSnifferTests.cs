using Modules.Messaging.Application;

namespace Modules.Messaging.UnitTests;

/// <summary>Spec messaging-media-reply R9: la firma del contenido tiene que casar con la extensión. Es el control que
/// atrapa un ejecutable renombrado, porque el PUT prefirmado no mira el contenido.</summary>
public sealed class OutboundMediaSnifferTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0];
    private static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0, 0, 0];

    [Theory]
    [InlineData(".jpg", OutboundMediaFormat.Jpeg)]
    [InlineData(".JPEG", OutboundMediaFormat.Jpeg)]
    [InlineData(".png", OutboundMediaFormat.Png)]
    [InlineData(".pdf", OutboundMediaFormat.Pdf)]
    [InlineData(".doc", OutboundMediaFormat.Doc)]
    [InlineData(".xls", OutboundMediaFormat.Xls)]
    [InlineData(".docx", OutboundMediaFormat.Docx)]
    [InlineData(".xlsx", OutboundMediaFormat.Xlsx)]
    public void ASignatureThatMatchesTheExtensionIsDetected(string extension, OutboundMediaFormat expected)
    {
        var header = expected switch
        {
            OutboundMediaFormat.Jpeg => Jpeg,
            OutboundMediaFormat.Png => Png,
            OutboundMediaFormat.Pdf => Pdf,
            OutboundMediaFormat.Doc or OutboundMediaFormat.Xls => Ole,
            _ => Zip,
        };

        Assert.Equal(expected, OutboundMediaSniffer.Detect(header, extension));
    }

    [Fact]
    public void AnExecutableRenamedToPdfIsRejected() => Assert.Null(OutboundMediaSniffer.Detect(Exe, ".pdf"));

    [Fact]
    public void AnOleFileWithAnOoxmlExtensionIsRejected() => Assert.Null(OutboundMediaSniffer.Detect(Ole, ".xlsx"));

    [Fact]
    public void AZipFileWithALegacyExtensionIsRejected() => Assert.Null(OutboundMediaSniffer.Detect(Zip, ".xls"));

    [Fact]
    public void AnUnknownExtensionOrAShortHeaderIsRejected()
    {
        Assert.Null(OutboundMediaSniffer.Detect(Jpeg, ".webp"));
        Assert.Null(OutboundMediaSniffer.Detect([0xFF, 0xD8], ".jpg"));
    }

    // Spec 2026-10-20 D4: cabeceras de archivos reales, recortadas a lo que mira el sniffer (64 bytes).
    private static byte[] Padded(params byte[][] parts)
    {
        var bytes = parts.SelectMany(part => part).ToList();
        while (bytes.Count < OutboundMediaSniffer.HeaderLength)
        {
            bytes.Add(0);
        }

        return [.. bytes];
    }

    /// <summary>ISO BMFF: caja <c>ftyp</c> de 24 bytes con la marca mayor y dos compatibles, como la escribe un teléfono.</summary>
    private static byte[] Iso(string brand) => Padded([0, 0, 0, 0x18], "ftyp"u8.ToArray(), System.Text.Encoding.ASCII.GetBytes(brand), [0, 0, 2, 0], "isommp41"u8.ToArray(), [0, 0, 0, 8], "free"u8.ToArray());

    // ID3v2.4 (10 bytes) y la primera trama MPEG-1 Layer III a 128 kbps.
    private static readonly byte[] Mp3WithId3 = Padded("ID3"u8.ToArray(), [4, 0, 0, 0, 0, 0, 0], [0xFF, 0xFB, 0x90, 0x64]);
    private static readonly byte[] Mp3WithoutId3 = Padded([0xFF, 0xFB, 0x90, 0x64, 0, 0, 0, 0]);
    private static readonly byte[] Mp3Mpeg2 = Padded([0xFF, 0xF3, 0x64, 0xC4]);

    // ADTS: MPEG-4 (FF F1) y MPEG-2 (FF F9), sin CRC, AAC-LC a 44,1 kHz.
    private static readonly byte[] AacMpeg4 = Padded([0xFF, 0xF1, 0x50, 0x80, 0x02, 0x1F, 0xFC]);
    private static readonly byte[] AacMpeg2 = Padded([0xFF, 0xF9, 0x50, 0x80, 0x02, 0x1F, 0xFC]);

    private static readonly byte[] AmrNb = Padded("#!AMR\n"u8.ToArray(), [0x3C, 0x48, 0xF5, 0x1F]);
    private static readonly byte[] AmrWb = Padded("#!AMR-WB\n"u8.ToArray(), [0x24, 0x18, 0x1E]);
    private static readonly byte[] AmrMultichannel = Padded("#!AMR_MC1.0\n"u8.ToArray());

    // RIFF/WAVE, EBML (WebM) y una QuickTime (.mov del iPhone) renombrados: no se mandan tal cual.
    private static readonly byte[] Wav = Padded("RIFF"u8.ToArray(), [0x24, 0x08, 0, 0], "WAVEfmt "u8.ToArray());

    private static byte[] Header(byte[] content) => content.Length >= OutboundMediaSniffer.HeaderLength ? content[..OutboundMediaSniffer.HeaderLength] : Padded(content);

    public static TheoryData<string, string, OutboundMediaFormat> VideoAndAudio() => new()
    {
        { ".mp4", "isom", OutboundMediaFormat.Mp4 },
        { ".mp4", "mp42", OutboundMediaFormat.Mp4 },
        { ".mp4", "mp41", OutboundMediaFormat.Mp4 },
        { ".mp4", "avc1", OutboundMediaFormat.Mp4 },
        { ".mp4", "iso5", OutboundMediaFormat.Mp4 },
        { ".MP4", "M4V ", OutboundMediaFormat.Mp4 },
        { ".3gp", "3gp4", OutboundMediaFormat.ThreeGp },
        { ".3gp", "3gp5", OutboundMediaFormat.ThreeGp },
        { ".m4a", "M4A ", OutboundMediaFormat.M4a },
        { ".m4a", "mp42", OutboundMediaFormat.M4a },
        { ".m4a", "isom", OutboundMediaFormat.M4a },
    };

    [Theory]
    [MemberData(nameof(VideoAndAudio))]
    public void AnIsoMediaFileIsDetectedByItsBrandAndExtension(string extension, string brand, OutboundMediaFormat expected) =>
        Assert.Equal(expected, OutboundMediaSniffer.Detect(Iso(brand), extension));

    // La marca separa 3GP de MP4/M4A; una QuickTime (qt) renombrada no pasa: es el .mov del iPhone que WhatsApp no recibe tal cual.
    [Theory]
    [InlineData(".mp4", "3gp4")]
    [InlineData(".m4a", "3gp4")]
    [InlineData(".3gp", "isom")]
    [InlineData(".3gp", "3g2a")]
    [InlineData(".mp4", "qt  ")]
    [InlineData(".m4a", "qt  ")]
    [InlineData(".mp4", "M4A ")]
    [InlineData(".m4a", "M4V ")]
    public void AnIsoMediaFileWithABrandOfAnotherFormatIsRejected(string extension, string brand) =>
        Assert.Null(OutboundMediaSniffer.Detect(Iso(brand), extension));

    [Fact]
    public void AnIsoMediaFileWithoutFtypFirstIsRejected()
    {
        // Una QuickTime vieja empieza por «wide»/«mdat», sin ftyp.
        Assert.Null(OutboundMediaSniffer.Detect(Padded([0, 0, 0, 8], "wide"u8.ToArray(), [0, 0, 0, 8], "mdat"u8.ToArray()), ".mp4"));
    }

    [Fact]
    public void AnMp3WithOrWithoutId3IsDetected()
    {
        Assert.Equal(OutboundMediaFormat.Mp3, OutboundMediaSniffer.Detect(Mp3WithId3, ".mp3"));
        Assert.Equal(OutboundMediaFormat.Mp3, OutboundMediaSniffer.Detect(Mp3WithoutId3, ".mp3"));
        Assert.Equal(OutboundMediaFormat.Mp3, OutboundMediaSniffer.Detect(Mp3Mpeg2, ".mp3"));
    }

    [Fact]
    public void AnAdtsAacIsDetected()
    {
        Assert.Equal(OutboundMediaFormat.Aac, OutboundMediaSniffer.Detect(AacMpeg4, ".aac"));
        Assert.Equal(OutboundMediaFormat.Aac, OutboundMediaSniffer.Detect(AacMpeg2, ".aac"));
    }

    // Las dos firmas comparten la sincronía FFF: decide la extensión, y la capa (00 en ADTS) las separa.
    [Fact]
    public void Mp3AndAacDoNotPassForEachOther()
    {
        Assert.Null(OutboundMediaSniffer.Detect(AacMpeg4, ".mp3"));
        Assert.Null(OutboundMediaSniffer.Detect(Mp3WithoutId3, ".aac"));
        Assert.Null(OutboundMediaSniffer.Detect(Mp3WithId3, ".aac"));
    }

    [Fact]
    public void AnAmrNarrowOrWideBandIsDetectedAndMultichannelIsNot()
    {
        Assert.Equal(OutboundMediaFormat.Amr, OutboundMediaSniffer.Detect(AmrNb, ".amr"));
        Assert.Equal(OutboundMediaFormat.Amr, OutboundMediaSniffer.Detect(AmrWb, ".amr"));
        Assert.Null(OutboundMediaSniffer.Detect(AmrMultichannel, ".amr"));
    }

    // .ogg sólo si es Opus: la primera página trae OpusHead (reusa OggOpusSniffer). Vorbis queda fuera.
    [Fact]
    public void AnOggIsDetectedOnlyIfItIsOpus()
    {
        Assert.Equal(OutboundMediaFormat.Ogg, OutboundMediaSniffer.Detect(Header(OggOpusFixture.VoiceNote()), ".ogg"));
        Assert.Null(OutboundMediaSniffer.Detect(Header(OggOpusFixture.VorbisFirstPage()), ".ogg"));
    }

    [Fact]
    public void AContainerThatIsNotSentAsIsIsRejectedUnderAnAllowedExtension()
    {
        Assert.Null(OutboundMediaSniffer.Detect(Wav, ".mp3"));
        Assert.Null(OutboundMediaSniffer.Detect(Header(OggOpusFixture.WebmHeader()), ".mp4"));
        Assert.Null(OutboundMediaSniffer.Detect(Header(OggOpusFixture.VoiceNote()), ".mp3"));
        Assert.Null(OutboundMediaSniffer.Detect(Iso("mp42"), ".ogg"));
        Assert.Null(OutboundMediaSniffer.Detect(Exe, ".mp4"));
    }

    // D4: OggOpusSniffer necesita 27 + segmentos + 8 bytes; con 8 no alcanzaba.
    [Fact]
    public void TheHeaderIsLongEnoughForAnOggOpusFirstPage() => Assert.Equal(64, OutboundMediaSniffer.HeaderLength);
}
