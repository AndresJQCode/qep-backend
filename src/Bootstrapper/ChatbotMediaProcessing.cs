using Modules.Messaging.Application;
using Modules.Storage.Application;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18 (Q6): Storage completa el archivo de un chatbot con las mismas piezas que validan y preparan lo que manda un asesor.
/// El contenido lo revisa <see cref="OutboundMediaSniffer"/> (la firma tiene que casar con la extensión) y una imagen por encima del tope
/// de Meta por link se recomprime con <see cref="IMessagingImageCompressor"/> (siempre JPEG).
/// </summary>
internal sealed class ChatbotMediaProcessing(IMessagingImageCompressor compressor) : IChatbotMediaProcessing
{
    public bool Matches(string name, ReadOnlySpan<byte> header) =>
        OutboundMediaSniffer.Detect(header, OutboundMediaPolicy.ExtensionOf(name)) is not null;

    public async Task<ProcessedChatbotImage?> ShrinkAsync(byte[] content, CancellationToken cancellationToken)
    {
        using var source = new MemoryStream(content, writable: false);
        return await compressor.CompressAsync(source, cancellationToken) is { } compressed
            ? new ProcessedChatbotImage(compressed.Content, "image/jpeg", ".jpg")
            : null;
    }
}
