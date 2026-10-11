namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-11 §6.2: Meta trata un audio como nota de voz sólo si es OGG con Opus (§3). Se decide por contenido:
/// los primeros 4 bytes son <c>OggS</c>, la versión del stream es 0 y el primer paquete de la primera página empieza
/// con <c>OpusHead</c> (RFC 7845 §5.1). No mira la duración: el tope de 5 minutos es de la UX y el tamaño ya acota.
/// </summary>
public static class OggOpusSniffer
{
    public const string MimeType = "audio/ogg";

    private const int PageHeaderLength = 27;

    private static ReadOnlySpan<byte> Capture => "OggS"u8;

    private static ReadOnlySpan<byte> OpusHead => "OpusHead"u8;

    public static bool IsOggOpus(ReadOnlySpan<byte> content)
    {
        if (content.Length < PageHeaderLength || !content[..Capture.Length].SequenceEqual(Capture) || content[4] != 0)
        {
            return false;
        }

        var segments = content[26];
        var bodyStart = PageHeaderLength + segments;
        if (content.Length < bodyStart + OpusHead.Length)
        {
            return false;
        }

        // RFC 3533: un paquete termina en el primer valor de lacing menor que 255.
        var firstPacketLength = 0;
        for (var index = 0; index < segments; index++)
        {
            var lacing = content[PageHeaderLength + index];
            firstPacketLength += lacing;
            if (lacing < 255)
            {
                break;
            }
        }

        return firstPacketLength >= OpusHead.Length && content.Slice(bodyStart, OpusHead.Length).SequenceEqual(OpusHead);
    }
}
