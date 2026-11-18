using Modules.Automations.Application;
using Modules.Automations.Domain.Engine;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Bootstrapper;

/// <summary>
/// Spec 2026-10-21 §5.3 y D-I4: los envíos del bot van por <see cref="IBotOutbound"/> de Mensajería, que valida los topes de Meta y
/// rechaza antes de enviar. Este adaptador sólo traduce: el <see cref="InteractiveMessage"/> del motor al <see cref="InteractiveSend"/>
/// de Mensajería, y el <see cref="BotSendResult"/> al <see cref="BotSendOutcome"/> que el procesador conoce.
/// </summary>
internal sealed class AutomationMessenger(IBotOutbound outbound) : IAutomationMessenger
{
    // El mismo código que IBotOutbound devuelve cuando el interactivo no pasa la validación (BotOutbound.cs, «messaging.interactive.invalid»).
    private const string InteractiveInvalid = "messaging.interactive.invalid";

    public async Task<BotSendOutcome> SendTextAsync(
        Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, string text, CancellationToken cancellationToken) =>
        Map(await outbound.SendAsync(new BotSend(tenantId, conversationId, chatbotId, clientId, text, null), cancellationToken));

    public async Task<BotSendOutcome> SendInteractiveAsync(
        Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, InteractiveMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (ToSend(message) is not { } send)
        {
            return new BotSendOutcome.Rejected(InteractiveInvalid);
        }

        return Map(await outbound.SendAsync(new BotSend(tenantId, conversationId, chatbotId, clientId, null, send), cancellationToken));
    }

    /// <summary>Spec 2026-11-18: el archivo va por link. Con botón es un solo <c>cta_url</c> con el archivo de encabezado y el pie de cuerpo (como lo
    /// dibuja el editor); sin botón, el archivo con su pie. El botón con una URL que no es web absoluta se rechaza como en
    /// <see cref="SendInteractiveAsync"/>. Un audio nunca llega con botón: el motor lo manda aparte.</summary>
    public async Task<BotSendOutcome> SendMediaAsync(
        Guid tenantId, Guid conversationId, Guid chatbotId, Guid clientId, BotMediaMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var file = message.File;
        if (KindOf(file.Kind) is not { } kind)
        {
            return new BotSendOutcome.Rejected(MessagingErrorCodes.MessageRejected);
        }

        var media = new BotMediaSend(kind, file.Url, file.FileName, file.ContentType, file.SizeBytes, file.FileId);
        if (message.UrlButton is not { } button)
        {
            return Map(await outbound.SendAsync(new BotSend(tenantId, conversationId, chatbotId, clientId, message.Caption, null, media), cancellationToken));
        }

        if (!IsWebUrl(button.Url, out var url))
        {
            return new BotSendOutcome.Rejected(InteractiveInvalid);
        }

        var header = new InteractiveMediaHeader(file.Kind, file.Url, kind == MessageKind.Document ? file.FileName : null);
        var cta = new CtaUrlSend(message.Caption ?? string.Empty, button.Label, url, MediaHeader: header);
        return Map(await outbound.SendAsync(new BotSend(tenantId, conversationId, chatbotId, clientId, null, cta, media), cancellationToken));
    }

    /// <summary>Los tipos del flujo en minúscula (<c>ChatbotMediaKinds</c>) al <see cref="MessageKind"/> de Mensajería.</summary>
    private static MessageKind? KindOf(string kind) => kind switch
    {
        "image" => MessageKind.Image,
        "video" => MessageKind.Video,
        "audio" => MessageKind.Audio,
        "document" => MessageKind.Document,
        _ => null,
    };

    private static bool IsWebUrl(string value, out Uri url) =>
        Uri.TryCreate(value, UriKind.Absolute, out url!) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);

    /// <summary><c>null</c> si el botón con URL no trae una URL web absoluta: sale de interpolar variables (D-I1) y un <c>{link}</c> vacío deja
    /// un texto que no es una URL. Se pide el esquema porque en Unix <c>Uri.TryCreate</c> lee <c>/p/1</c> como <c>file:///p/1</c>; que sea https
    /// lo valida Mensajería. La lista va en una sola sección sin título (§5.4: el bloque no tiene secciones).</summary>
    private static InteractiveSend? ToSend(InteractiveMessage message) => message switch
    {
        InteractiveMessage.Buttons buttons => new ButtonsSend(buttons.Body, [.. buttons.Items.Select(item => new InteractiveButton(item.Id, item.Title))]),
        InteractiveMessage.List list => new ListSend(
            list.Body, list.ButtonLabel, [new ListSection(null, [.. list.Rows.Select(row => new ListRow(row.Id, row.Title, row.Description))])]),
        InteractiveMessage.CtaUrl cta => IsWebUrl(cta.Url, out var url) ? new CtaUrlSend(cta.Body, cta.Label, url) : null,
        _ => throw new ArgumentOutOfRangeException(nameof(message), message.GetType().Name, "Mensaje interactivo desconocido."),
    };

    /// <summary>Un envío que ya había salido con el mismo <c>clientId</c> cuenta como enviado (reintento de la fila, P2-R5). Sin conexión
    /// utilizable es un rechazo con el código de Mensajería: la corrida termina <c>Failed</c> y pasa a un asesor.</summary>
    private static BotSendOutcome Map(BotSendResult result) => result switch
    {
        BotSendResult.Sent sent => new BotSendOutcome.Sent(sent.MessageId),
        BotSendResult.NotAutomated => new BotSendOutcome.NotAutomated(),
        BotSendResult.WindowClosed => new BotSendOutcome.WindowClosed(),
        BotSendResult.Blocked => new BotSendOutcome.Blocked(),
        BotSendResult.ConnectionUnavailable unavailable =>
            new BotSendOutcome.Rejected(MessagingErrorCodes.ConnectionUnavailable, unavailable.Reason, unavailable.ConnectionName),
        BotSendResult.Rejected rejected => new BotSendOutcome.Rejected(rejected.Code),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.GetType().Name, "Resultado de envío desconocido."),
    };
}
