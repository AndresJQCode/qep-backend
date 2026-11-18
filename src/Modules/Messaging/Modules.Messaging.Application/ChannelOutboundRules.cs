using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-29 §6.5: lo que un canal le permite a un saliente de texto libre. <see cref="Window"/> es la ventana de atención
/// desde el último entrante (<c>null</c> = el canal no tiene ventana); <see cref="TextMaxLength"/>, el largo máximo de un texto.</summary>
public sealed record ChannelOutboundRule(TimeSpan? Window, int TextMaxLength);

/// <summary>
/// Spec 2026-10-29 §6.5: una regla por canal, en un solo lugar, para el envío del bot y para quien parte una respuesta larga (el agente de
/// IA). WhatsApp: 24 h y <see cref="SendMessageValidator.TextMaxLength"/>; Instagram: 24 h y
/// <see cref="SendMessageValidator.InstagramTextMaxLength"/>. <see cref="Conversation.WindowLength"/> sigue siendo el valor de WhatsApp.
/// </summary>
public static class ChannelOutboundRules
{
    private static readonly ChannelOutboundRule WhatsApp = new(Conversation.WindowLength, SendMessageValidator.TextMaxLength);

    private static readonly ChannelOutboundRule Instagram = new(TimeSpan.FromHours(24), SendMessageValidator.InstagramTextMaxLength);

    public static ChannelOutboundRule For(ConversationChannel channel) => channel switch
    {
        ConversationChannel.WhatsApp => WhatsApp,
        ConversationChannel.Instagram => Instagram,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "The channel has no outbound rule."),
    };

    /// <summary>Si la ventana del canal sigue abierta en <paramref name="now"/>. Sin entrante no hay ventana que abrir; un canal sin ventana
    /// siempre está abierto.</summary>
    public static bool IsWindowOpen(ConversationChannel channel, DateTimeOffset? lastInboundAt, DateTimeOffset now) =>
        For(channel).Window is not { } window || (lastInboundAt is { } last && last.Add(window) > now);
}
