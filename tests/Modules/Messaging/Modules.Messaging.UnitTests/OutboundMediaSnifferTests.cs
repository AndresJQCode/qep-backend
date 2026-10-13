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
}
