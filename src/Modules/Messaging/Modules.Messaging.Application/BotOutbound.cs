using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-21 D-I4: lo que el motor de chatbots manda como el bot. <see cref="Text"/> es <c>null</c> cuando viaja
/// <see cref="Interactive"/>; el texto guardado del mensaje es entonces el cuerpo del interactivo.</summary>
public sealed record BotSend(Guid TenantId, Guid ConversationId, Guid ChatbotId, Guid ClientId, string? Text, InteractiveSend? Interactive);

public abstract record BotSendResult
{
    public sealed record Sent(Guid MessageId, bool AlreadySent) : BotSendResult;

    /// <summary>La conversación no está en manos del bot: resuelta, con asesor, o sin automatización <c>Active</c>.</summary>
    public sealed record NotAutomated : BotSendResult;

    public sealed record WindowClosed : BotSendResult;

    /// <summary>Spec 2026-10-24 D-T11: <paramref name="Reason"/> es un código de spec §3.4 (<c>paused</c>, <c>secret_unreadable</c>,
    /// <c>credentials_rejected</c>, …) y <paramref name="ConnectionName"/> el nombre de la conexión de WhatsApp, para el hilo del traspaso.</summary>
    public sealed record ConnectionUnavailable(string? Reason = null, string? ConnectionName = null) : BotSendResult;

    public sealed record Rejected(string Code) : BotSendResult;
}

public interface IBotOutbound
{
    Task<BotSendResult> SendAsync(BotSend send, CancellationToken cancellationToken);
}

/// <summary>
/// Spec 2026-10-21 D-I4. El mismo camino que el envío de un asesor (reclamo → conexión → Meta → tabla de respuestas), pero sin
/// <c>IExecutionContext</c>, membresía, permiso ni autoasignación: el bot no es una persona y responder no toma la conversación.
/// Las guardas son de estado, no de autorización: sólo escribe si la conversación está <c>Open</c>, sin asesor y con la
/// automatización <c>Active</c>, y dentro de la ventana de 24 h (la ventana se mira antes de reclamar: no deja fila).
/// </summary>
public sealed class BotOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock) : IBotOutbound
{
    /// <summary>Spec 2026-10-24 §3.4: Meta rechazó el token al enviar.</summary>
    public const string CredentialsRejected = "credentials_rejected";

    public async Task<BotSendResult> SendAsync(BotSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);
        // Lectura sin seguimiento a propósito: el motor llama varias veces dentro de un mismo scope, y una entidad rastreada
        // ocultaría un Take del asesor entre dos mensajes del bot.
        var conversation = await conversations.FindAsync(send.TenantId, send.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return new BotSendResult.NotAutomated();
        }

        // Lo que ya salió, ya salió, aunque un asesor haya tomado la conversación en el medio (como el envío humano, SendMessage.cs).
        if (await outbound.FindByClientIdAsync(conversation.Id, send.ClientId, cancellationToken) is { Status: not MessageStatus.Failed } sent)
        {
            return new BotSendResult.Sent(sent.Id, AlreadySent: true);
        }

        if (conversation.Status != ConversationStatus.Open
            || conversation.AssignedMemberId is not null
            || conversation.AutomationStatus != ConversationAutomationStatus.Active
            // Instagram no tiene automatización ni interactivos (D-I1): nunca es del bot.
            || conversation.Channel != ConversationChannel.WhatsApp)
        {
            return new BotSendResult.NotAutomated();
        }

        var now = clock.UtcNow;
        if (conversation.LastInboundAt is not { } lastInbound || lastInbound.Add(Conversation.WindowLength) <= now)
        {
            return new BotSendResult.WindowClosed();
        }

        if (send.Interactive is { } interactive && InteractiveSendValidation.Validate(interactive).Count > 0)
        {
            return new BotSendResult.Rejected("messaging.interactive.invalid");
        }

        // La misma regla que el texto de un asesor (SendMessageValidator): el flujo lo escribe un admin y puede traer un \0 o pasarse
        // de largo, y eso no debe llegar a Meta ni dejar fila.
        if (send.Interactive is null ? !SendMessageValidator.IsAcceptableText(send.Text) : !AreAcceptable(send.Interactive))
        {
            return new BotSendResult.Rejected(MessagingErrorCodes.MessageRejected);
        }

        // Con interactivo, su cuerpo es la única fuente del texto guardado.
        var text = send.Interactive?.Body ?? send.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new BotSendResult.Rejected(MessagingErrorCodes.MessageRejected);
        }

        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, send.TenantId, conversation.ConnectionId, send.ClientId, text, SentByMemberId: null, now,
            Kind: send.Interactive is null ? MessageKind.Text : MessageKind.Interactive,
            DetailsJson: send.Interactive is null ? null : InteractiveSendJson.ToDetailsJson(send.Interactive),
            SentByChatbotId: send.ChatbotId);
        await using var claim = await outbound.ClaimAsync(draft, cancellationToken);

        // Dos envíos en vuelo con el mismo ClientId: el segundo esperó el candado del primero.
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return new BotSendResult.Sent(inFlight.Id, AlreadySent: true);
        }

        var resolution = await connections.ResolveSenderWithReasonAsync(send.TenantId, conversation.ConnectionId, cancellationToken);
        if (resolution.Sender is not { } sender)
        {
            await claim.RollbackAsync(CancellationToken.None);
            return new BotSendResult.ConnectionUnavailable(resolution.UnavailableReason, resolution.ConnectionName);
        }

        // Desde acá todo cierre va con CancellationToken.None, como en SendMessageHandler: si Meta ya aceptó, cancelar no puede borrar la fila.
        var target = SendTarget.For(conversation.UserId, conversation.WaId);
        var callback = SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D");
        var result = send.Interactive is { } card
            ? await meta.SendInteractiveAsync(sender, target, card, callback, null, CancellationToken.None)
            : await meta.SendTextAsync(sender, target, text, callback, null, CancellationToken.None);
        try
        {
            await new OutboundSettlement(repository, connections, notifier, receipts, clock)
                .SettleAsync(send.TenantId, conversation.ConnectionId, claim, result, clock.UtcNow, metaMediaId: null, afterRollback: null, seen: conversation.UnreadCount);
        }
        catch (MessagingDomainException exception) when (exception.Code == MessagingErrorCodes.ConnectionUnavailable)
        {
            // La liquidación sólo tira este código cuando Meta rechazó la credencial o el número; la conexión ya quedó NeedsAttention.
            return new BotSendResult.ConnectionUnavailable(CredentialsRejected, resolution.ConnectionName);
        }
        catch (MessagingDomainException exception) when (exception.Code == MessagingErrorCodes.WindowClosed)
        {
            return new BotSendResult.WindowClosed();
        }
        catch (MessagingDomainException exception)
        {
            return new BotSendResult.Rejected(exception.Code);
        }

        return new BotSendResult.Sent(claim.MessageId, AlreadySent: false);
    }

    /// <summary>Cada texto del interactivo, también ids y la URL, pasa la regla del asesor: sin caracteres de control y ≤ 4096. Los
    /// vacíos y los topes de Meta, más cortos, ya los miró <see cref="InteractiveSendValidation"/>.</summary>
    private static bool AreAcceptable(InteractiveSend send)
    {
        IEnumerable<string?> texts = [send.Body, send.Header, send.Footer];
        texts = send switch
        {
            ButtonsSend buttons => texts.Concat(buttons.Buttons.SelectMany(b => new[] { b.Id, b.Title })),
            ListSend list => texts.Append(list.ButtonLabel).Concat(list.Sections.SelectMany(section =>
                section.Rows.SelectMany(r => new[] { r.Id, r.Title, r.Description }).Prepend(section.Title))),
            CtaUrlSend cta => texts.Concat([cta.DisplayText, cta.Url.OriginalString]),
            _ => texts,
        };
        return texts.All(text => text is null || (text.Length <= SendMessageValidator.TextMaxLength && !SendMessageValidator.HasForbiddenControl(text)));
    }
}
