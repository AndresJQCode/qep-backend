using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Un <c>change</c> de un webhook de Meta (spec §3, «Mensaje entrante», «Estados», «account_update»).</summary>
internal abstract record WebhookChange;

internal sealed record MessagesChange(string PhoneNumberId, IReadOnlyList<InboundMessage> Messages, IReadOnlyList<StatusUpdate> Statuses) : WebhookChange;

internal sealed record AccountUpdateChange(string WabaId, string Event, string? BanState) : WebhookChange;

internal sealed record UnknownChange(string Field) : WebhookChange;

internal sealed record InboundMedia(string MetaMediaId, string MimeType, string? Sha256, string? FileName);

/// <summary>Ya con el <c>kind</c> del mapa de §8.7 y el texto/leyenda/detalles separados como los guarda §7.3.</summary>
internal sealed record InboundMessage(
    string Wamid,
    string WaId,
    string? ProfileName,
    DateTimeOffset OccurredAt,
    MessageKind Kind,
    string? Text,
    string? Caption,
    string? DetailsJson,
    InboundMedia? Media);

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
