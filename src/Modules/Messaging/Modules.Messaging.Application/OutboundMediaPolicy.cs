using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Messaging-media-reply R2, R9 y R10: qué adjuntos acepta el asesor, con qué mime y hasta qué tamaño. Una sola tabla para la
/// sesión de subida y para la confirmación: la extensión decide el formato, el mime tiene que ser el canónico de ese formato y
/// el contenido lo verifica <see cref="OutboundMediaSniffer"/>.
/// </summary>
public static class OutboundMediaPolicy
{
    /// <summary>Se comprime después, así que el tope de entrada es mayor que el de lo que viaja a Meta.</summary>
    public const long MaxImageBytes = 25L * 1024 * 1024;

    /// <summary>El máximo de Meta para un documento.</summary>
    public const long MaxDocumentBytes = 100L * 1024 * 1024;

    /// <summary>La extensión en minúsculas y con punto, o vacío si el nombre no tiene.</summary>
    public static string ExtensionOf(string fileName) => Path.GetExtension(fileName.Trim()).ToLowerInvariant();

    public static OutboundMediaFormat? FormatOf(string extension) => extension.Trim().ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => OutboundMediaFormat.Jpeg,
        ".png" => OutboundMediaFormat.Png,
        ".pdf" => OutboundMediaFormat.Pdf,
        ".doc" => OutboundMediaFormat.Doc,
        ".xls" => OutboundMediaFormat.Xls,
        ".docx" => OutboundMediaFormat.Docx,
        ".xlsx" => OutboundMediaFormat.Xlsx,
        _ => null,
    };

    public static string MimeTypeOf(OutboundMediaFormat format) => format switch
    {
        OutboundMediaFormat.Jpeg => "image/jpeg",
        OutboundMediaFormat.Png => "image/png",
        OutboundMediaFormat.Pdf => "application/pdf",
        OutboundMediaFormat.Doc => "application/msword",
        OutboundMediaFormat.Xls => "application/vnd.ms-excel",
        OutboundMediaFormat.Docx => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        OutboundMediaFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static bool IsImage(OutboundMediaFormat format) => format is OutboundMediaFormat.Jpeg or OutboundMediaFormat.Png;

    public static long MaxBytesOf(OutboundMediaFormat format) => IsImage(format) ? MaxImageBytes : MaxDocumentBytes;

    /// <summary>Spec 2026-10-14 §3 y decisión 12: Instagram acepta imagen y PDF, de hasta 25 MB; Word y Excel no.</summary>
    public const long InstagramMaxBytes = 25L * 1024 * 1024;

    public static bool IsAllowed(OutboundMediaFormat format, ConversationChannel channel) =>
        channel != ConversationChannel.Instagram || IsImage(format) || format == OutboundMediaFormat.Pdf;

    public static long MaxBytesOf(OutboundMediaFormat format, ConversationChannel channel) =>
        channel == ConversationChannel.Instagram ? InstagramMaxBytes : MaxBytesOf(format);

    /// <summary>§3: el <c>attachment.type</c> de la Messaging API de Instagram.</summary>
    public static string InstagramAttachmentType(OutboundMediaFormat format) => IsImage(format) ? "image" : "file";
}
