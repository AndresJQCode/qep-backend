namespace Modules.Storage.Application;

/// <summary>
/// Spec 2026-11-18 (multimedia del bloque «Mensaje», Q6): lo que Storage necesita de Mensajería para completar un archivo
/// <see cref="Domain.FileOwnerType.ChatbotMedia"/>. El inspector de la consola no conoce video ni audio, así que el contenido lo revisa
/// el mismo sniffer que valida lo que manda un asesor, y una imagen por encima del tope de Meta por link se comprime con el mismo
/// compresor. Implemented in Bootstrapper over Messaging's OutboundMediaSniffer and IMessagingImageCompressor; Storage no registra
/// ninguna implementación.
/// </summary>
public interface IChatbotMediaProcessing
{
    /// <summary>Si la cabecera (los primeros 64 bytes como mucho) es de verdad lo que dice la extensión del nombre.</summary>
    bool Matches(string name, ReadOnlySpan<byte> header);

    /// <summary>La imagen comprimida. null = unreadable image.</summary>
    Task<ProcessedChatbotImage?> ShrinkAsync(byte[] content, CancellationToken cancellationToken);
}

public sealed record ProcessedChatbotImage(byte[] Content, string MimeType, string Extension);
