using Modules.Automations.Application;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-11-18: lo que un chatbot puede mandar es lo que un asesor manda por WhatsApp. Lee <see cref="OutboundMediaPolicy"/> en vivo
/// (columna WhatsApp), así que si Mensajería cambia un formato o un tope, el bot lo sigue sin tocar Automations. El tipo es el nombre del
/// <c>MessageKind</c> en minúscula, el mismo de <c>ChatbotMediaKinds</c> del flujo.
/// </summary>
internal sealed class ChatbotMediaPolicy : IChatbotMediaPolicy
{
    public ChatbotMediaRule? RuleFor(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (OutboundMediaPolicy.FormatOf(OutboundMediaPolicy.ExtensionOf(fileName)) is not { } format)
        {
            return null;
        }

        var rule = OutboundMediaPolicy.RuleOf(format);

        // El flujo del chatbot no tiene el tipo sticker (ChatbotMediaKinds): sólo lo manda un asesor.
        return rule.Kind == MessageKind.Sticker ? null : new ChatbotMediaRule(KindOf(rule), rule.MimeType, rule.WhatsAppMaxBytes);
    }

    /// <summary>El tipo de un archivo ya guardado, por la extensión de su nombre (tras comprimir, <c>.jpg</c>). <c>null</c> si Mensajería
    /// dejó de aceptar el formato.</summary>
    public static string? KindOfFileName(string fileName) =>
        OutboundMediaPolicy.FormatOf(OutboundMediaPolicy.ExtensionOf(fileName)) is { } format && OutboundMediaPolicy.RuleOf(format) is { Kind: not MessageKind.Sticker } rule
            ? KindOf(rule)
            : null;

    private static string KindOf(OutboundMediaRule rule) => rule.Kind.ToString().ToLowerInvariant();
}
