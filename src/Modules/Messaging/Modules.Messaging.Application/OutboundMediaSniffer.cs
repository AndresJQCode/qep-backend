namespace Modules.Messaging.Application;

/// <summary>Formatos de adjunto que el asesor puede enviar (decisión del owner 2026-10-12; video y audio, spec 2026-10-20). webp queda
/// fuera: Meta lo trata como sticker. Qué canal acepta cada uno y hasta cuánto, en <see cref="OutboundMediaPolicy"/>.</summary>
public enum OutboundMediaFormat
{
    Jpeg,
    Png,
    Pdf,
    Doc,
    Xls,
    Docx,
    Xlsx,
    Mp4,
    ThreeGp,
    Mp3,
    M4a,
    Aac,
    Amr,
    Ogg,
}

/// <summary>
/// Messaging-media-reply R9: el contenido manda, no el nombre ni el mime. La firma de los primeros bytes tiene que casar
/// con la extensión: jpeg <c>FFD8FF</c>, png <c>89504E470D0A1A0A</c>, pdf <c>%PDF-</c>, OLE <c>D0CF11E0A1B11AE1</c> (.doc/.xls)
/// y zip <c>PK\x03\x04</c> (.docx/.xlsx). OLE es compartido con .ppt/.msg y un zip renombrado a .docx pasa: es el riesgo
/// aceptado por el owner, sin antivirus.
/// </summary>
public static class OutboundMediaSniffer
{
    /// <summary>Con 8 bytes alcanza para todas las firmas.</summary>
    public const int HeaderLength = 8;

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> PdfSignature => [0x25, 0x50, 0x44, 0x46, 0x2D];

    private static ReadOnlySpan<byte> OleSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static ReadOnlySpan<byte> ZipSignature => [0x50, 0x4B, 0x03, 0x04];

    /// <summary><c>null</c> si la extensión no es una de las permitidas o la firma no casa con ella.</summary>
    public static OutboundMediaFormat? Detect(ReadOnlySpan<byte> header, string extension)
    {
        return extension.Trim().ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" when header.StartsWith(JpegSignature) => OutboundMediaFormat.Jpeg,
            ".png" when header.StartsWith(PngSignature) => OutboundMediaFormat.Png,
            ".pdf" when header.StartsWith(PdfSignature) => OutboundMediaFormat.Pdf,
            ".doc" when header.StartsWith(OleSignature) => OutboundMediaFormat.Doc,
            ".xls" when header.StartsWith(OleSignature) => OutboundMediaFormat.Xls,
            ".docx" when header.StartsWith(ZipSignature) => OutboundMediaFormat.Docx,
            ".xlsx" when header.StartsWith(ZipSignature) => OutboundMediaFormat.Xlsx,
            _ => null,
        };
    }
}
