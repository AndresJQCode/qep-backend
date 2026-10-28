using System.Buffers.Binary;
using Concentus;
using Concentus.Oggfile;
using Modules.Messaging.Application;

namespace Modules.Messaging.Infrastructure.Media;

/// <summary>
/// Spec 2026-10-14 decisión 16: OGG/Opus → WAV PCM 16 bits mono a 16 kHz, con un decodificador administrado (Concentus), sin ffmpeg
/// en la imagen. 16 kHz y no 48: Opus decodifica nativamente a esa tasa, la voz no pierde nada audible y una nota de 5 minutos pesa
/// 9,6 MB en vez de 28,8, por debajo del tope de 25 MB de Instagram.
/// </summary>
internal sealed class ConcentusOpusTranscoder : IOpusTranscoder
{
    public const int SampleRate = 16_000;
    public const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int WavHeaderLength = 44;

    static ConcentusOpusTranscoder()
    {
        // Sólo el decodificador administrado: la imagen no trae libopus, y buscar la nativa en cada arranque es lento y variable.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    public byte[] ToWav(byte[] oggOpus)
    {
        ArgumentNullException.ThrowIfNull(oggOpus);
        // El mismo chequeo del validador, por contenido: un OGG que no es Opus (Vorbis) hace que el decodificador administrado se
        // quede dando vueltas sobre paquetes que nunca va a entender, en vez de fallar.
        if (!OggOpusSniffer.IsOggOpus(oggOpus))
        {
            throw new InvalidDataException("The file is not an OGG/Opus stream.");
        }

        using var pcm = new MemoryStream();
        // Espacio para la cabecera; se escribe al final, cuando se conoce el largo.
        pcm.SetLength(WavHeaderLength);
        pcm.Position = WavHeaderLength;
        try
        {
            using var input = new MemoryStream(oggOpus, writable: false);
            var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
            var reader = new OpusOggReadStream(decoder, input);
            var samples = new byte[2];
            var decodedAny = false;
            while (reader.HasNextPacket)
            {
                var packet = reader.DecodeNextPacket();
                if (packet is null)
                {
                    continue;
                }

                decodedAny = true;
                foreach (var sample in packet)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(samples, sample);
                    pcm.Write(samples, 0, 2);
                }
            }

            if (!decodedAny)
            {
                throw new InvalidDataException(reader.LastError ?? "The OGG stream has no Opus packets.");
            }
        }
        catch (Exception exception) when (exception is not InvalidDataException and not OutOfMemoryException)
        {
            // Concentus lanza sus propias excepciones ante un stream corrupto; todas significan lo mismo para quien llama.
            throw new InvalidDataException("The OGG/Opus stream could not be decoded.", exception);
        }

        var bytes = pcm.ToArray();
        WriteHeader(bytes, bytes.Length - WavHeaderLength);
        return bytes;
    }

    /// <summary>RIFF/WAVE canónico, PCM lineal.</summary>
    private static void WriteHeader(Span<byte> wav, int dataLength)
    {
        const int blockAlign = Channels * BitsPerSample / 8;
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(wav[8..]);
        "fmt "u8.CopyTo(wav[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(wav[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav[28..], SampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(wav[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(wav[34..], BitsPerSample);
        "data"u8.CopyTo(wav[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(wav[40..], dataLength);
    }
}
