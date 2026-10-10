namespace Modules.Messaging.Domain;

public enum ConversationStatus
{
    Open,
    Resolved,
}

/// <summary>Spec 2026-10-09 §7.1: en <c>messages</c> viajan como smallint con estos valores (excepción
/// documentada a los enums por texto). La API sigue mandando los nombres.</summary>
public enum MessageDirection
{
    Inbound = 1,
    Outbound = 2,

    /// <summary>Spec 2026-10-10 §6.1.5: un evento del sistema en el hilo; nunca va a WhatsApp.</summary>
    System = 3,
}

public enum MessageKind
{
    Text = 1,
    Image = 2,
    Video = 3,
    Audio = 4,
    Document = 5,
    Sticker = 6,
    Location = 7,
    Contacts = 8,
    Reaction = 9,
    Interactive = 10,
    Template = 11,
    Unsupported = 12,

    /// <summary>Spec 2026-10-10 §6.1.5: un evento del sistema en el hilo; nunca va a WhatsApp.</summary>
    Event = 13,
}

/// <summary>El orden numérico <b>es</b> el de avance (§7.5): un acuse nunca retrocede.</summary>
public enum MessageStatus
{
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4,
}
