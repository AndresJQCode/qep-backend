namespace Modules.Messaging.Application;

/// <summary>Formatos de adjunto que el asesor puede enviar (decisión del owner 2026-10-12; video y audio, spec 2026-10-20; PowerPoint,
/// texto y sticker, 2026-11-22). Es todo lo que la Cloud API de WhatsApp admite. Meta trata el webp como sticker, no como imagen: sale
/// con <c>type: "sticker"</c>. Qué canal acepta cada uno y hasta cuánto, en <see cref="OutboundMediaPolicy"/>.</summary>
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
    Ppt,
    Pptx,
    Txt,
    Webp,
}

/// <summary>
/// Messaging-media-reply R9: el contenido manda, no el nombre ni el mime. La firma de los primeros bytes tiene que casar
/// con la extensión: jpeg <c>FFD8FF</c>, png <c>89504E470D0A1A0A</c>, pdf <c>%PDF-</c>, OLE <c>D0CF11E0A1B11AE1</c> (.doc/.xls)
/// y zip <c>PK\x03\x04</c> (.docx/.xlsx). OLE es compartido con .ppt/.msg y un zip renombrado a .docx pasa: es el riesgo
/// aceptado por el owner, sin antivirus.
/// <para>Spec 2026-10-20 D4, video y audio: ISO BMFF (<c>ftyp</c> en los bytes 4-7) para .mp4/.3gp/.m4a, con la marca mayor (bytes
/// 8-11) separando 3GP de MP4/M4A y dejando fuera la QuickTime (<c>qt</c>, el .mov del iPhone); .mp3 = <c>ID3</c> o una trama MPEG
/// (sincronía <c>FF Ex</c> con capa distinta de 00); .aac = ADTS (sincronía con capa 00: <c>FF F1</c>/<c>FF F9</c>); .amr =
/// <c>#!AMR\n</c> o <c>#!AMR-WB\n</c>; .ogg = OGG con <c>OpusHead</c> en la primera página (<see cref="OggOpusSniffer"/>). La
/// extensión decide entre firmas que se solapan. No mira el códec (D9): un MP4 HEVC pasa y, si Meta lo rechaza, el mensaje queda
/// <c>Failed</c> con el motivo de Meta.</para>
/// <para>2026-11-22: .ppt comparte la firma OLE con .doc/.xls y .pptx la del zip con .docx/.xlsx (mismo riesgo aceptado). .txt no tiene
/// firma: se acepta UTF-8 válido sin bytes de control (salvo tabulación y saltos de línea), lo que deja fuera un binario renombrado y el
/// UTF-16. Como el sniffer sólo ve <see cref="HeaderLength"/> bytes, un carácter multibyte cortado en el límite no invalida el texto.
/// .webp = contenedor RIFF/WEBP con un primer chunk VP8, VP8L o VP8X; <see cref="IsAnimatedWebp"/> separa el sticker animado del
/// estático, que tienen topes distintos en Meta.</para>
/// </summary>
public static class OutboundMediaSniffer
{
    /// <summary>D4: <see cref="OggOpusSniffer.IsOggOpus"/> necesita 27 + segmentos + 8 bytes; 64 alcanza para todas las firmas.</summary>
    public const int HeaderLength = 64;

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> PdfSignature => [0x25, 0x50, 0x44, 0x46, 0x2D];

    private static ReadOnlySpan<byte> OleSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private static ReadOnlySpan<byte> ZipSignature => [0x50, 0x4B, 0x03, 0x04];

    private static ReadOnlySpan<byte> Ftyp => "ftyp"u8;

    private static ReadOnlySpan<byte> Id3 => "ID3"u8;

    private static ReadOnlySpan<byte> AmrNarrowBand => "#!AMR\n"u8;

    private static ReadOnlySpan<byte> AmrWideBand => "#!AMR-WB\n"u8;

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
            ".ppt" when header.StartsWith(OleSignature) => OutboundMediaFormat.Ppt,
            ".pptx" when header.StartsWith(ZipSignature) => OutboundMediaFormat.Pptx,
            ".txt" when IsPlainUtf8Text(header) => OutboundMediaFormat.Txt,
            ".webp" when IsWebp(header) => OutboundMediaFormat.Webp,
            ".mp4" when BrandOf(header) is { } brand && IsMp4VideoBrand(brand) => OutboundMediaFormat.Mp4,
            ".3gp" when BrandOf(header) is { } brand && brand.StartsWith("3gp", StringComparison.Ordinal) => OutboundMediaFormat.ThreeGp,
            ".m4a" when BrandOf(header) is { } brand && IsM4aBrand(brand) => OutboundMediaFormat.M4a,
            ".mp3" when header.StartsWith(Id3) || IsMpegAudioFrame(header) => OutboundMediaFormat.Mp3,
            ".aac" when IsAdtsFrame(header) => OutboundMediaFormat.Aac,
            ".amr" when header.StartsWith(AmrNarrowBand) || header.StartsWith(AmrWideBand) => OutboundMediaFormat.Amr,
            ".ogg" when OggOpusSniffer.IsOggOpus(header) => OutboundMediaFormat.Ogg,
            _ => null,
        };
    }

    /// <summary><c>true</c> si es un WebP extendido (chunk <c>VP8X</c>) con la bandera de animación (bit 1 de las banderas, byte 20).
    /// Un VP8 o VP8L simple nunca está animado.</summary>
    public static bool IsAnimatedWebp(ReadOnlySpan<byte> header) =>
        IsWebp(header) && header.Slice(12, 4).SequenceEqual("VP8X"u8) && header.Length > 20 && (header[20] & 0x02) != 0;

    // RIFF (0-3), tamaño (4-7), WEBP (8-11) y el primer chunk (12-15): VP8 (con espacio), VP8L o VP8X.
    private static bool IsWebp(ReadOnlySpan<byte> header) =>
        header.Length >= 16
        && header[..4].SequenceEqual("RIFF"u8)
        && header.Slice(8, 4).SequenceEqual("WEBP"u8)
        && (header.Slice(12, 4).SequenceEqual("VP8 "u8) || header.Slice(12, 4).SequenceEqual("VP8L"u8) || header.Slice(12, 4).SequenceEqual("VP8X"u8));

    private static bool IsPlainUtf8Text(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        foreach (var value in header)
        {
            if ((value < 0x20 && value is not (0x09 or 0x0A or 0x0D)) || value == 0x7F)
            {
                return false;
            }
        }

        if (System.Text.Unicode.Utf8.IsValid(header))
        {
            return true;
        }

        // El archivo sigue más allá de lo leído: un carácter de hasta 4 bytes pudo quedar cortado en el límite.
        if (header.Length < HeaderLength)
        {
            return false;
        }

        for (var cut = 1; cut <= 3; cut++)
        {
            if (System.Text.Unicode.Utf8.IsValid(header[..^cut]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>La marca mayor de la caja <c>ftyp</c>, que en un MP4 bien formado es la primera.</summary>
    private static string? BrandOf(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header.Slice(4, 4).SequenceEqual(Ftyp) ? System.Text.Encoding.ASCII.GetString(header.Slice(8, 4)) : null;

    // isom/iso2..iso9, mp41/mp42, avc1 y M4V (iTunes). M4A es sólo audio y no sale como video; qt es QuickTime.
    private static bool IsMp4VideoBrand(string brand) => IsGenericIsoBrand(brand) || brand is "avc1" or "M4V ";

    private static bool IsM4aBrand(string brand) => IsGenericIsoBrand(brand) || brand == "M4A ";

    private static bool IsGenericIsoBrand(string brand) =>
        brand.StartsWith("iso", StringComparison.Ordinal) || brand.StartsWith("mp4", StringComparison.Ordinal);

    // Sincronía de 11 bits; la capa (bits 2-1 del segundo byte) es 01, 10 u 11 en MPEG audio y 00 en ADTS.
    private static bool IsMpegAudioFrame(ReadOnlySpan<byte> header) =>
        header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0 && (header[1] & 0x06) != 0;

    // ADTS: sincronía de 12 bits y capa 00 (FF F0/F1 MPEG-4, FF F8/F9 MPEG-2; el último bit es la ausencia de CRC).
    private static bool IsAdtsFrame(ReadOnlySpan<byte> header) => header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xF6) == 0xF0;
}
