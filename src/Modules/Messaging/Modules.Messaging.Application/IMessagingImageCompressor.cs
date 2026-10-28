namespace Modules.Messaging.Application;

/// <summary>
/// Messaging-media-reply R12: toda imagen del asesor se recomprime antes de ir a Meta (lado mayor 1600 px, JPEG q80, sin
/// metadatos). El adaptador usa ImageSharp en Bootstrapper; Application no referencia SixLabors.
/// </summary>
public interface IMessagingImageCompressor
{
    /// <summary><c>null</c> si los bytes no decodifican como jpeg o png, o pasan del tope de megapíxeles. El stream es de quien llama.</summary>
    Task<CompressedImage?> CompressAsync(Stream source, CancellationToken cancellationToken);
}

/// <summary>Siempre <c>image/jpeg</c>.</summary>
public sealed record CompressedImage(byte[] Content, int Width, int Height);
