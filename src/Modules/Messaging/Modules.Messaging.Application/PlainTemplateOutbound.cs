using BuildingBlocks.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Lo que el núcleo de <see cref="PlainTemplateOutbound"/> manda: una plantilla del sistema sin documento, al hilo del número. <c>ClientId</c>
/// es la clave de idempotencia del reclamo por <c>(conversation_id, client_id)</c>: el cobro pasa uno nuevo en cada aviso y el aviso de
/// invitación de Mayorista pasa el id de su mensaje del outbox, así un reintento no manda dos. <c>LegacyPositionalKeys</c> es el orden de una
/// fila POSITIONAL vieja; <c>null</c> si el tipo nunca tuvo una.
/// </summary>
internal sealed record PlainTemplateSend(
    Guid TenantId, Guid ConnectionId, Guid SentByMemberId, Guid ClientId,
    string WaId,
    Guid? CustomerId,
    string RecipientName,
    ApprovedTemplate Template,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string>? LegacyPositionalKeys);

internal abstract record PlainTemplateSendResult
{
    public sealed record Sent(Guid MessageId, Guid ConversationId) : PlainTemplateSendResult;

    /// <summary><paramref name="Reason"/> es un código de spec 2026-10-24 §3.4 (<c>paused</c>, <c>credentials_rejected</c>, …).</summary>
    public sealed record ConnectionUnavailable(string? Reason) : PlainTemplateSendResult;

    public sealed record Failed(string Code) : PlainTemplateSendResult;
}

/// <summary>
/// El núcleo que comparten <see cref="BillingTemplateOutbound"/>, <see cref="WholesaleInvitationNoticeOutbound"/> y <see cref="AppointmentReminderOutbound"/>: la plantilla (con variables
/// con nombre; las filas viejas POSITIONAL siguen saliendo), sin encabezado de documento, al hilo del número en esa conexión. Es
/// <see cref="QuotationTemplateOutbound"/> sin documento ni R2 y por la misma razón no usa <see cref="OutboundGuards"/>: el envío lo autoriza
/// el módulo que llama, no se rechaza si la conversación está resuelta o asignada a otra persona y no cambia su estado ni su asignación.
/// Tampoco resta el no leído (<c>seen: 0</c>): nadie leyó el hilo para enviar. Una falla tardía (Meta rechazando la credencial al enviar)
/// puede dejar una conversación nueva vacía, como en <see cref="StartConversationHandler"/>: la conversación se abre antes del reclamo.
/// Interno a propósito: cada módulo entra por su propio puerto público, con su propio resultado.
/// </summary>
internal sealed class PlainTemplateOutbound(
    IConversationQueries conversations,
    IConversationRepository repository,
    IOutboundMessages outbound,
    IWhatsAppCloudClient meta,
    IMessagingConnectionDirectory connections,
    IMessagingNotifier notifier,
    ReadReceipts receipts,
    IClock clock,
    IConversationOpener opener,
    TemplateSenderValues senderValues)
{
    public async Task<PlainTemplateSendResult> SendAsync(PlainTemplateSend send, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(send);

        // 1. El teléfono, y después las reglas de Meta sobre cada valor que la fila usa (error 132018), antes de tocar nada. Un valor que la
        // fila no usa no la bloquea: no viaja. Una fila vieja POSITIONAL se arma en el orden de LegacyPositionalKeys.
        if (!IsAcceptable(send))
        {
            return new PlainTemplateSendResult.Failed(MessagingErrorCodes.MessageRejected);
        }

        var values = new Dictionary<string, string>(send.Values, StringComparer.Ordinal);
        await senderValues.AddAsync(send.TenantId, send.SentByMemberId, SystemTemplateSend.VariablesOf(send.Template), values, cancellationToken);
        if (SystemTemplateSend.Build(send.Template, values, send.LegacyPositionalKeys) is not { } built)
        {
            return new PlainTemplateSendResult.Failed(MessagingErrorCodes.MessageRejected);
        }

        // 2.
        var resolution = await connections.ResolveSenderWithReasonAsync(send.TenantId, send.ConnectionId, cancellationToken);
        if (resolution.Sender is not { } sender)
        {
            return new PlainTemplateSendResult.ConnectionUnavailable(resolution.UnavailableReason);
        }

        // 3. Por el teléfono de destino, sin asignar a nadie.
        var now = clock.UtcNow;
        var opened = await opener.OpenByPhoneAsync(send.TenantId, send.ConnectionId, send.WaId, send.CustomerId, send.RecipientName, now, cancellationToken);
        var conversation = await conversations.FindAsync(send.TenantId, opened.ConversationId, cancellationToken)
            ?? throw new InvalidOperationException("The conversation that was just opened could not be read.");

        // 4. El clientId de quien llama: el reclamo es idempotente por (conversation_id, client_id).
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, send.TenantId, send.ConnectionId, send.ClientId, built.Text,
            send.SentByMemberId, now, Kind: MessageKind.Template,
            DetailsJson: built.DetailsJson);
        await using var claim = await outbound.ClaimAsync(draft, cancellationToken);

        // Una Failed con el reintento apagado es terminal, como en OutboundGuards.TakeInFlightAsync: no se reenvía aunque quien llama
        // repita su clientId (el procesador del aviso de Mayorista reintenta siempre con el mismo).
        if (!claim.Inserted && claim.Existing is { Status: MessageStatus.Failed, RetryDisabled: true })
        {
            await claim.RollbackAsync(CancellationToken.None);
            return new PlainTemplateSendResult.Failed(MessagingErrorCodes.MessageNotRetriable);
        }

        // Un reintento con el mismo clientId cuya fila ya salió (o está en camino) no vuelve a llamar a Meta: devuelve la que hay. Una
        // Failed con reintento sí se reenvía, sobre la misma fila.
        if (!claim.Inserted && claim.Existing is { } existing && existing.Status != MessageStatus.Failed)
        {
            await claim.RollbackAsync(CancellationToken.None);
            return new PlainTemplateSendResult.Sent(claim.MessageId, conversation.Id);
        }

        // 5 y 6. Desde acá todo va con CancellationToken.None, como en el envío del asesor: si Meta ya aceptó, cancelar no puede borrar la fila.
        try
        {
            var result = await meta.SendTemplateAsync(
                sender, SendTarget.ForTemplate(conversation.UserId, conversation.WaId),
                built.Send,
                SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"),
                CancellationToken.None);
            await new OutboundSettlement(repository, connections, notifier, receipts, clock)
                .SettleAsync(send.TenantId, send.ConnectionId, claim, result, clock.UtcNow, metaMediaId: null, afterRollback: null, seen: 0);
        }
        catch (MessagingDomainException exception) when (exception.Code == MessagingErrorCodes.ConnectionUnavailable)
        {
            // La liquidación sólo tira este código cuando Meta rechazó la credencial o el número; la conexión ya quedó NeedsAttention.
            return new PlainTemplateSendResult.ConnectionUnavailable(BotOutbound.CredentialsRejected);
        }
        catch (MessagingDomainException exception)
        {
            // Failed: la fila queda, para que el hilo muestre qué no salió.
            return new PlainTemplateSendResult.Failed(exception.Code);
        }

        return new PlainTemplateSendResult.Sent(claim.MessageId, conversation.Id);
    }

    private static bool IsAcceptable(PlainTemplateSend send) =>
        // El teléfono es la clave de la conversación: con otra forma, el opener no podría usarlo.
        send.WaId is { Length: > 0 and <= Conversation.WaIdMaxLength } waId && waId.All(char.IsAsciiDigit);
}
