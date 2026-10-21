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

    public sealed record ConnectionUnavailable : BotSendResult;

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

        var sender = await connections.ResolveSenderAsync(send.TenantId, conversation.ConnectionId, cancellationToken);
        if (sender is null)
        {
            await claim.RollbackAsync(CancellationToken.None);
            return new BotSendResult.ConnectionUnavailable();
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
            return new BotSendResult.ConnectionUnavailable();
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
}
