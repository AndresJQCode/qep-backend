using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>La plantilla del catálogo ya validada (aprobada, enviable, claves exactas) con sus valores.</summary>
internal sealed record ResolvedTemplate(MessageTemplateDto Template, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// Spec 2026-10-11 §5.2: el pegamento que comparten <see cref="SendTemplateMessageHandler"/> y <see cref="StartConversationHandler"/>:
/// resolver la plantilla contra el catálogo y, con la conversación ya cargada, recorrer las guardas de <see cref="OutboundGuards"/>
/// (sin ventana), reclamar, enviar por Meta y liquidar. Nada de esto abre transacciones propias: es el mismo reclamo del texto.
/// </summary>
internal sealed class TemplateOutbound(OutboundGuards guards, IWhatsAppCloudClient meta, IMessageTemplates templates, IMessagingCustomerDirectory customers)
{
    public OutboundGuards Guards => guards;

    public async Task<ResolvedTemplate> ResolveAsync(
        Guid tenantId, Guid connectionId, string name, string language, IReadOnlyDictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var catalog = await templates.ListAsync(tenantId, connectionId, refresh: false, cancellationToken);
        var template = TemplateCatalog.RequireSendable(catalog, name, language, parameters.Keys);
        // El validador ya garantizó que ningún valor es nulo.
        return new ResolvedTemplate(template, parameters.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal));
    }

    /// <summary>Lo ya enviado con ese <c>clientId</c> primero (sin mirar catálogo, estado ni dueño); si no, lo mismo que el texto salvo
    /// la ventana: status Open → dueño (sin asignar, enviar es tomar) → reclamo → conexión Active → Meta → tabla de respuestas.</summary>
    public async Task<MessageDto?> FindAlreadySentAsync(Guid tenantId, OutboundTarget target, Guid clientId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return await guards.FindAlreadySentAsync(target.Conversation.Id, clientId, cancellationToken) is { } sent
            ? await guards.ToDtoAsync(tenantId, OutboundGuards.ExistingRow(target.Conversation.Id, sent, clientId), cancellationToken)
            : null;
    }

    public async Task<MessageDto> SendAsync(Guid tenantId, OutboundTarget target, Guid clientId, ResolvedTemplate resolved, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(resolved);
        var conversation = target.Conversation;
        var permit = await guards.AdmitTemplateAsync(tenantId, target, cancellationToken);
        var template = resolved.Template;
        var built = TemplateCatalog.BuildSend(template, resolved.Parameters);
        var text = built.Text;
        var details = built.DetailsJson;
        var draft = new OutboundDraft(
            Guid.CreateVersion7(), conversation.Id, tenantId, conversation.ConnectionId, clientId, text, target.Member, permit.Now,
            ReplyToMessageId: null, ReplyToWamid: null, MessageKind.Template, Media: null, details);
        await using var claim = await guards.ClaimAsync(draft, cancellationToken);

        // Desde acá, todo cierre del reclamo va con CancellationToken.None, igual que el texto: si Meta ya aceptó, cortar por la
        // pestaña cerrada borraría la fila y el reintento con el mismo clientId mandaría la plantilla dos veces.
        if (await OutboundGuards.TakeInFlightAsync(claim) is { } inFlight)
        {
            return await guards.ToDtoAsync(tenantId, OutboundGuards.ExistingRow(conversation.Id, inFlight, clientId), cancellationToken);
        }

        var sender = await guards.ResolveSenderAsync(tenantId, conversation.ConnectionId, claim, cancellationToken);
        var result = await meta.SendTemplateAsync(
            sender, SendTarget.ForTemplate(conversation.UserId, conversation.WaId), built.Send, SendMessageHandler.CallbackPrefix + claim.MessageId.ToString("D"),
            CancellationToken.None);
        var answeredAt = guards.UtcNow;

        // Decisión 10: Meta avisa que la persona apagó el marketing; se anota en su ficha antes de cerrar el reclamo (que lanza).
        // La falla del aviso no le quita el 422 a quien envía: el reclamo se deshace al desecharse, y reintentar es seguro porque
        // Meta no aceptó el mensaje.
        if (result is { Outcome: SendOutcome.GraphError, Code: MetaErrorCodes.MarketingOptOut } && conversation.CustomerId is { } customerId)
        {
            await customers.MarkMarketingOptOutAsync(tenantId, customerId, CancellationToken.None);
        }

        await guards.SettleAsync(tenantId, conversation.ConnectionId, claim, result, answeredAt, metaMediaId: null, afterRollback: null);
        return await guards.ToDtoAsync(
            tenantId,
            new MessageRow(claim.MessageId, conversation.Id, MessageDirection.Outbound, MessageKind.Template, text, null, details, MessageStatus.Sent, null, answeredAt,
                target.Member, clientId, null),
            CancellationToken.None);
    }
}
