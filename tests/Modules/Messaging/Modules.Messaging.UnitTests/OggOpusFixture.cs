using System.Buffers.Binary;

namespace Modules.Messaging.UnitTests;

/// <summary>
/// Spec 2026-10-11 §10: una nota de voz OGG/Opus mínima, armada byte a byte (RFC 3533 y RFC 7845) en vez de bajar un
/// binario de internet: tres páginas con su CRC —<c>OpusHead</c> (BOS), <c>OpusTags</c> y un frame de silencio de
/// 20 ms (EOS)—, mono a 48 kHz. Es el mismo contenedor que entrega <c>opus-recorder</c>, en chiquito, y un reproductor
/// lo decodifica (20 ms de silencio). Copia en <c>Modules.Messaging.IntegrationTests</c>: los proyectos de prueba no se
/// referencian entre sí.
/// </summary>
internal static class OggOpusFixture
{
    private const uint Serial = 0x5145_5021;
    private const ushort PreSkip = 312;

    // Un frame CELT de silencio (TOC 0xF8: 20 ms, banda completa, mono, un frame).
    private static readonly byte[] SilentFrame = [0xF8, 0xFF, 0xFE];

    private static readonly byte[] OpusTags = [.. "OpusTags"u8, 3, 0, 0, 0, .. "qep"u8, 0, 0, 0, 0];

    // Cabecera de identificación de Vorbis: «\x01vorbis», versión, canales, 48 kHz, tres bitrates, bloques y framing.
    private static readonly byte[] VorbisIdentification = [0x01, .. "vorbis"u8, .. new byte[4], 1, 0x80, 0xBB, 0, 0, .. new byte[12], 0xB8, 0x01];

    /// <summary>Las tres páginas: lo que decodifica un reproductor.</summary>
    public static byte[] VoiceNote() => [.. FirstPage(), .. Page(0x00, 0, 1, OpusTags), .. Page(0x04, PreSkip + 960, 2, SilentFrame)];

    /// <summary>Sólo la página BOS con <c>OpusHead</c>: 27 + 1 + 19 = 47 bytes.</summary>
    public static byte[] FirstPage() => Page(0x02, 0, 0, OpusHead());

    /// <summary>Un OGG válido, pero con Vorbis: WhatsApp no lo trata como nota de voz.</summary>
    public static byte[] VorbisFirstPage() => Page(0x02, 0, 0, VorbisIdentification);

    /// <summary>EBML: lo que graba <c>MediaRecorder</c> en Chrome y Edge (WebM/Opus).</summary>
    public static byte[] WebmHeader() => [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, 0x01, .. new byte[40]];

    /// <summary>ISO BMFF: lo que graba <c>MediaRecorder</c> en Safari (MP4/AAC).</summary>
    public static byte[] Mp4Header() => [0x00, 0x00, 0x00, 0x18, .. "ftypmp42"u8, .. new byte[40]];

    private static byte[] OpusHead()
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1; // versión
        head[9] = 1; // canales
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), PreSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48_000);
        // Ganancia 0 y familia de mapeo 0: los bytes 16 a 18 quedan en cero.
        return head;
    }

    private static byte[] Page(byte headerType, long granulePosition, uint sequence, byte[] packet)
    {
        var lacing = new List<byte>();
        var remaining = packet.Length;
        while (remaining >= 255)
        {
            lacing.Add(255);
            remaining -= 255;
        }

        lacing.Add((byte)remaining);
        var page = new byte[27 + lacing.Count + packet.Length];
        "OggS"u8.CopyTo(page);
        page[5] = headerType;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granulePosition);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), Serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)lacing.Count;
        lacing.CopyTo(page, 27);
        packet.CopyTo(page, 27 + lacing.Count);
        // El CRC se calcula con su propio campo en cero (RFC 3533 §6).
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), Crc32(page));
        return page;
    }

    // CRC-32 de Ogg: polinomio 0x04C11DB7, sin reflejar, semilla 0 y sin XOR final.
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0u;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1;
            }
        }

        return crc;
    }
}
