using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace Modules.Storage.Infrastructure.Imaging;

// Configuración única de ImageSharp para todo lo que se decodifica en Storage. Sólo registra los
// formatos que FileUploadPolicy deja subir (JPEG, PNG y WebP). TIFF queda fuera a propósito: es
// donde viven los cinco avisos de ImageSharp 3.1.12 (ver NuGetAuditSuppress en
// Directory.Build.props), y ImageSharp detecta el formato por el contenido y no por el MIME, así
// que con la configuración por defecto un TIFF subido con MIME de imagen llegaría al decodificador
// (el bucle infinito de BigTIFF). Sin el módulo de TIFF registrado, esos avisos son inalcanzables
// y un TIFF falla como storage.image.invalid. Si Storage empieza a aceptar otro formato, se
// registra aquí.
internal static class ImageSharpConfiguration
{
    private static readonly Configuration Restricted = new(
        new JpegConfigurationModule(),
        new PngConfigurationModule(),
        new WebpConfigurationModule());

    public static DecoderOptions DecoderOptions { get; } = new() { Configuration = Restricted };
}
