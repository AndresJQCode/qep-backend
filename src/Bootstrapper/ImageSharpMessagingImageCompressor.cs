using Modules.Messaging.Application;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Bootstrapper;

/// <summary>
/// Messaging-media-reply R12: recomprime la imagen que el asesor va a enviar. Decodifica con una <see cref="Configuration"/>
/// propia que sólo registra JPEG y PNG (los únicos formatos permitidos): sin TIFF ni WebP, que son los vectores de los avisos
/// de ImageSharp 3.1.12 (ver <c>NuGetAuditSuppress</c> en Directory.Build.props). No reutiliza la de Storage porque es
/// <c>internal</c> de ese módulo. <c>TargetSize</c> (sólo si la imagen pasa de 1600 px) deja que el decodificador de JPEG baje
/// la escala al leer, así una foto de 40 MP no se materializa a tamaño completo.
/// </summary>
internal sealed class ImageSharpMessagingImageCompressor : IMessagingImageCompressor
{
    private const int MaxSide = 1600;
    private const int Quality = 80;
    private const long MaxPixels = 40_000_000;

    private static readonly Configuration Restricted = new(new JpegConfigurationModule(), new PngConfigurationModule());

    private static readonly DecoderOptions Options = new() { Configuration = Restricted };

    public async Task<CompressedImage?> CompressAsync(Stream source, CancellationToken cancellationToken)
    {
        // Identify consume lo que lee y un stream que no se recorre (el GET de R2 detrás del hash del handler) no se puede
        // rebobinar para decodificar: se copia a un búfer propio. Son a lo sumo 25 MB, el tope de una imagen.
        if (!source.CanSeek)
        {
            var buffer = new MemoryStream();
            await using (buffer.ConfigureAwait(false))
            {
                await source.CopyToAsync(buffer, cancellationToken);
                buffer.Position = 0;
                return await CompressAsync(buffer, cancellationToken);
            }
        }

        try
        {
            // Los bytes los controla quien sube el archivo: se mide antes de decodificar.
            var info = await Image.IdentifyAsync(Options, source, cancellationToken);
            if ((long)info.Width * info.Height > MaxPixels)
            {
                return null;
            }

            if (source.CanSeek)
            {
                source.Position = 0;
            }

            // TargetSize deja que el decodificador baje la escala al leer, pero también amplía lo chico: sólo se pide
            // cuando la imagen realmente pasa del lado máximo.
            var decoderOptions = Math.Max(info.Width, info.Height) > MaxSide
                ? new DecoderOptions { Configuration = Restricted, TargetSize = new Size(MaxSide, MaxSide) }
                : Options;
            using var image = await Image.LoadAsync<Rgba32>(decoderOptions, source, cancellationToken);
            image.Mutate(context =>
            {
                context.AutoOrient();
                if (Math.Max(image.Width, image.Height) > MaxSide)
                {
                    context.Resize(new ResizeOptions { Size = new Size(MaxSide, MaxSide), Mode = ResizeMode.Max });
                }

                // JPEG no tiene alfa: lo transparente se aplana en blanco, no en negro.
                context.BackgroundColor(Color.White);
            });

            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;

            using var output = new MemoryStream();
            await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = Quality, SkipMetadata = true }, cancellationToken);
            return new CompressedImage(output.ToArray(), image.Width, image.Height);
        }
        catch (Exception exception) when (exception is ImageFormatException or UnknownImageFormatException
            or InvalidImageContentException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // Firma de jpeg/png con contenido que no decodifica (o un formato fuera del registro): no es una imagen.
            return null;
        }
    }
}
