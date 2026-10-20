using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Una fila de <see cref="OutboundMediaPolicy"/>: <paramref name="InstagramMaxBytes"/> en <c>null</c> = Instagram no lo acepta.</summary>
public sealed record OutboundMediaRule(OutboundMediaFormat Format, string Extension, MessageKind Kind, string MimeType, long WhatsAppMaxBytes, long? InstagramMaxBytes);

/// <summary>
/// Messaging-media-reply R2, R9 y R10, y spec 2026-10-20 (video y audio) D2: qué adjuntos acepta el asesor, con qué mime, qué kind
/// sale y hasta qué tamaño en cada canal. Una sola tabla para la sesión de subida y para la confirmación: la extensión decide el
/// formato, el mime tiene que ser el canónico de ese formato y el contenido lo verifica <see cref="OutboundMediaSniffer"/>.
/// <para>Video y audio salen de la documentación pública de Meta y están <b>en borrador hasta V-1..V-3</b> (spec 2026-10-20): lo que
/// Meta real no acepte se quita de acá. No hay transcodificación: lo que un canal no recibe tal cual no está en la tabla.</para>
/// </summary>
public static class OutboundMediaPolicy
{
    /// <summary>Se comprime después, así que el tope de entrada es mayor que el de lo que viaja a Meta.</summary>
    public const long MaxImageBytes = 25L * 1024 * 1024;

    /// <summary>El máximo de Meta para un documento.</summary>
    public const long MaxDocumentBytes = 100L * 1024 * 1024;

    /// <summary>El máximo de WhatsApp para un video o un audio (spec 2026-10-20, «Formatos y topes por canal»).</summary>
    public const long MaxWhatsAppVideoAudioBytes = 16L * 1024 * 1024;

    /// <summary>Spec 2026-10-14 §3 y decisión 12: Instagram acepta hasta 25 MB por adjunto; Word y Excel no.</summary>
    public const long InstagramMaxBytes = 25L * 1024 * 1024;

    private static readonly OutboundMediaRule[] Table =
    [
        new(OutboundMediaFormat.Jpeg, ".jpg", MessageKind.Image, "image/jpeg", MaxImageBytes, InstagramMaxBytes),
        new(OutboundMediaFormat.Png, ".png", MessageKind.Image, "image/png", MaxImageBytes, InstagramMaxBytes),
        new(OutboundMediaFormat.Pdf, ".pdf", MessageKind.Document, "application/pdf", MaxDocumentBytes, InstagramMaxBytes),
        new(OutboundMediaFormat.Doc, ".doc", MessageKind.Document, "application/msword", MaxDocumentBytes, null),
        new(OutboundMediaFormat.Xls, ".xls", MessageKind.Document, "application/vnd.ms-excel", MaxDocumentBytes, null),
        new(OutboundMediaFormat.Docx, ".docx", MessageKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", MaxDocumentBytes, null),
        new(OutboundMediaFormat.Xlsx, ".xlsx", MessageKind.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", MaxDocumentBytes, null),

        // Instagram: sólo MP4 en video (V-2).
        new(OutboundMediaFormat.Mp4, ".mp4", MessageKind.Video, "video/mp4", MaxWhatsAppVideoAudioBytes, InstagramMaxBytes),
        new(OutboundMediaFormat.ThreeGp, ".3gp", MessageKind.Video, "video/3gpp", MaxWhatsAppVideoAudioBytes, null),

        // Instagram: mp3, m4a y aac (V-2, decisión del owner 2026-10-20). El .ogg sólo si es Opus: lo confirma el sniffer.
        new(OutboundMediaFormat.Mp3, ".mp3", MessageKind.Audio, "audio/mpeg", MaxWhatsAppVideoAudioBytes, InstagramMaxBytes),
        new(OutboundMediaFormat.M4a, ".m4a", MessageKind.Audio, "audio/mp4", MaxWhatsAppVideoAudioBytes, InstagramMaxBytes),
        new(OutboundMediaFormat.Aac, ".aac", MessageKind.Audio, "audio/aac", MaxWhatsAppVideoAudioBytes, InstagramMaxBytes),
        new(OutboundMediaFormat.Amr, ".amr", MessageKind.Audio, "audio/amr", MaxWhatsAppVideoAudioBytes, null),
        new(OutboundMediaFormat.Ogg, ".ogg", MessageKind.Audio, OggOpusSniffer.MimeType, MaxWhatsAppVideoAudioBytes, null),
    ];

    public static IReadOnlyList<OutboundMediaRule> Rules => Table;

    /// <summary>La extensión en minúsculas y con punto, o vacío si el nombre no tiene.</summary>
    public static string ExtensionOf(string fileName) => Path.GetExtension(fileName.Trim()).ToLowerInvariant();

    public static OutboundMediaFormat? FormatOf(string extension)
    {
        var normalized = extension.Trim().ToLowerInvariant();
        if (normalized == ".jpeg")
        {
            return OutboundMediaFormat.Jpeg;
        }

        return Array.Find(Table, rule => rule.Extension == normalized)?.Format;
    }

    public static OutboundMediaRule RuleOf(OutboundMediaFormat format) =>
        Array.Find(Table, rule => rule.Format == format) ?? throw new ArgumentOutOfRangeException(nameof(format));

    public static string MimeTypeOf(OutboundMediaFormat format) => RuleOf(format).MimeType;

    /// <summary>Spec 2026-10-20 D3: el kind del mensaje que sale (<c>Image</c>, <c>Video</c>, <c>Audio</c> o <c>Document</c>).</summary>
    public static MessageKind KindOf(OutboundMediaFormat format) => RuleOf(format).Kind;

    public static bool IsImage(OutboundMediaFormat format) => KindOf(format) == MessageKind.Image;

    /// <summary>Spec 2026-10-20 D7: WhatsApp no admite <c>caption</c> en audio; guardar un texto que nadie recibe es el anti-goal.</summary>
    public static bool AcceptsCaption(OutboundMediaFormat format) => KindOf(format) != MessageKind.Audio;

    public static bool IsAllowed(OutboundMediaFormat format, ConversationChannel channel) => MaxBytesOf(format, channel) is not null;

    /// <summary>El tope del canal, o <c>null</c> si el canal no acepta el formato.</summary>
    public static long? MaxBytesOf(OutboundMediaFormat format, ConversationChannel channel)
    {
        var rule = RuleOf(format);
        return channel == ConversationChannel.Instagram ? rule.InstagramMaxBytes : rule.WhatsAppMaxBytes;
    }

    /// <summary>§3 y spec 2026-10-20 D6: el <c>attachment.type</c> de la Messaging API de Instagram.</summary>
    public static string InstagramAttachmentType(OutboundMediaFormat format) => KindOf(format) switch
    {
        MessageKind.Image => "image",
        MessageKind.Video => "video",
        MessageKind.Audio => "audio",
        _ => "file",
    };
}
