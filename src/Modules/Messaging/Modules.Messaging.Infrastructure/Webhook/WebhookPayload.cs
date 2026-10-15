using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Un <c>change</c> de un webhook de Meta (spec §3, «Mensaje entrante», «Estados», «account_update»).</summary>
internal abstract record WebhookChange;

/// <summary><paramref name="NumberChanges"/>: los mensajes <c>system</c> de tipo <c>user_changed_user_id</c>
/// (spec 2026-10-10 §8.3); no son mensajes del hilo. <paramref name="SkippedWithoutUserId"/>: los mensajes que se
/// saltaron por no traer un <c>from_user_id</c> válido (§8.1, «se salta y se registra»); el procesador los registra.</summary>
internal sealed record MessagesChange(
    string PhoneNumberId,
    IReadOnlyList<InboundMessage> Messages,
    IReadOnlyList<StatusUpdate> Statuses,
    IReadOnlyList<UserIdChange> NumberChanges,
    int SkippedWithoutUserId = 0) : WebhookChange;

/// <summary>Spec 2026-10-10 §8.3: la persona pasó de un BSUID a otro; el teléfono nuevo, si Meta lo manda.</summary>
internal sealed record UserIdChange(string Previous, string Current, string? WaId);

/// <summary>El campo <c>user_id_update</c> (§8.3). Llega por WABA; el <paramref name="PhoneNumberId"/> sólo si viene en
/// <c>metadata</c>.</summary>
internal sealed record UserIdUpdateChange(string WabaId, string? PhoneNumberId, UserIdChange Change) : WebhookChange;

internal sealed record AccountUpdateChange(string WabaId, string Event, string? BanState) : WebhookChange;

/// <summary>Spec 2026-10-14 §4.3: <c>message_template_status_update</c>. Llega por WABA; <c>MetaTemplateId</c> viene como número o string.
/// <c>Reason</c> «NONE» lo descarta el dominio.</summary>
internal sealed record TemplateStatusChange(string WabaId, string MetaTemplateId, string? Name, string? Language, string Event, string? Reason) : WebhookChange;

internal sealed record UnknownChange(string Field) : WebhookChange;

/// <summary>Decisión del owner 2026-10-12 (P1): el botón de respuesta rápida de una plantilla (<c>type = button</c>), con su texto y su
/// payload. Sólo ese tipo lo trae: ni un texto escrito a mano ni la respuesta a un mensaje interactivo.</summary>
internal sealed record QuickReplyButton(string Text, string? Payload);

internal sealed record InboundMedia(string MetaMediaId, string MimeType, string? Sha256, string? FileName);

/// <summary>Ya con el <c>kind</c> del mapa de §8.7 y el texto/leyenda/detalles separados como los guarda §7.3.
/// Spec 2026-10-10 §8.1: <paramref name="UserId"/> (el BSUID) es la clave y siempre viene; el teléfono, el usuario y
/// la cita (<paramref name="QuotedWamid"/>, el <c>context.id</c>) son opcionales.</summary>
internal sealed record InboundMessage(
    string Wamid,
    string UserId,
    string? WaId,
    string? ProfileName,
    string? Username,
    string? ParentUserId,
    DateTimeOffset OccurredAt,
    MessageKind Kind,
    string? Text,
    string? Caption,
    string? DetailsJson,
    InboundMedia? Media,
    string? QuotedWamid,
    QuickReplyButton? QuickReply = null);

internal sealed record StatusUpdate(
    string Wamid,
    MessageStatus Status,
    DateTimeOffset OccurredAt,
    Guid? CallbackMessageId,
    int? ErrorCode,
    string? ErrorTitle);

/// <summary>Spec §8.7, «Mapa de tipos de Meta a kind»; <c>button</c> → Interactive (D-M6); todo lo demás Unsupported.</summary>
internal static class MessageKindMap
{
    public static MessageKind FromMetaType(string? type) => type switch
    {
        "text" => MessageKind.Text,
        "image" => MessageKind.Image,
        "video" => MessageKind.Video,
        "audio" => MessageKind.Audio,
        "document" => MessageKind.Document,
        "sticker" => MessageKind.Sticker,
        "location" => MessageKind.Location,
        "contacts" => MessageKind.Contacts,
        "reaction" => MessageKind.Reaction,
        "interactive" or "button" => MessageKind.Interactive,
        _ => MessageKind.Unsupported,
    };

    public static bool HasMedia(MessageKind kind) =>
        kind is MessageKind.Image or MessageKind.Video or MessageKind.Audio or MessageKind.Document or MessageKind.Sticker;
}
