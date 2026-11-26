using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

public sealed record GetReplyOptionsQuery(Guid TenantId, Guid TicketId) : IQuery<SupportReplyOptionsDto>;

/// <summary>
/// <c>reply-options</c> (plan E17b): qué canal puede usar el asesor y qué va a pasar, con las mismas reglas del envío
/// (<see cref="SupportReplyRules"/>). La plantilla a un ticket con conversación se comporta como el hilo (plan E14), así que repite su
/// disponibilidad; sin conversación, necesita el celular de la foto y una conexión con la plantilla aprobada. Fase 4b (Tarea 8):
/// <c>Formal</c> dice si se puede mandar la respuesta formal y por dónde, con las mismas reglas del envío.
/// </summary>
public sealed class GetReplyOptionsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    SupportTicketScope scope,
    ISupportReplyChannels channels,
    ITenantClock tenantClock,
    ISupportLetterhead letterhead)
    : IQueryHandler<GetReplyOptionsQuery, SupportReplyOptionsDto>
{
    public async Task<SupportReplyOptionsDto> HandleAsync(GetReplyOptionsQuery query, CancellationToken cancellationToken)
    {
        await scope.EnsureAccessAsync(query.TenantId, cancellationToken);
        var ticket = await scope.ViewAsync(query.TenantId, query.TicketId, cancellationToken);
        // Spec 2026-11-22 (plan D11): una tarea no tiene a quién responder.
        SupportTicketScope.EnsureExternal(ticket);
        // Plan F12: un anónimo sólo se responde por la página de consulta; ningún otro canal llega a él.
        if (ticket.IsAnonymous)
        {
            var portalOnly = new SupportChannelOptionDto(false, SupportChannelReasons.PortalOnly, []);
            return new SupportReplyOptionsDto(
                new SupportThreadOptionDto(false, SupportChannelReasons.PortalOnly, null, null, false),
                portalOnly,
                portalOnly,
                nameof(SupportReplyChannel.Portal),
                new SupportChannelOptionDto(true, null, []),
                new SupportFormalOptionsDto(
                    ticket.RequiresFormalResponse,
                    await FormalBlockedReasonAsync(ticket, cancellationToken),
                    SupportChannelReasons.PortalOnly,
                    []));
        }

        var now = (await tenantClock.GetAsync(query.TenantId, cancellationToken)).UtcNow;
        var messagingOn = await SupportConversationRules.IsEnabledAsync(
            tenantModules, query.TenantId, TenantModuleKeys.Messaging, cancellationToken);

        SupportThreadOptionDto thread;
        SupportChannelOptionDto template;
        if (!messagingOn)
        {
            thread = new SupportThreadOptionDto(false, SupportChannelReasons.MessagingDisabled, null, null, false);
            template = new SupportChannelOptionDto(false, SupportChannelReasons.MessagingDisabled, []);
        }
        else
        {
            var availability = await SupportReplyRules.ThreadAsync(channels, ticket, now, cancellationToken);
            // Plan E19: un adjunto sólo sale como texto en el hilo (la plantilla no lo lleva), y se sube por la sesión de subida de
            // Mensajería, que exige su permiso: sin él, la pantalla no ofrece adjuntar. Responder además exige gestionar tickets.
            // Instagram no lleva pie en un adjunto (el texto de la respuesta se perdería): sólo WhatsApp.
            var canAttach = availability.Route is { Delivery: SupportReplyDelivery.Text }
                && ticket.ConversationChannel == SupportConversationRef.WhatsApp
                && executionContext.HasPermission(SupportPermissions.Manage)
                && executionContext.HasPermission(SupportReplyAttachmentRules.UploadPermission);
            thread = new SupportThreadOptionDto(
                availability.Route is not null, availability.Reason, availability.Route?.Delivery.ToString(), availability.WindowClosesAt,
                canAttach);
            template = ticket.ConversationId is not null
                ? new SupportChannelOptionDto(availability.Route is not null, availability.Reason, [])
                : await TemplateWithoutConversationAsync(ticket, cancellationToken);
        }

        var email = string.IsNullOrWhiteSpace(ticket.Requester?.Email)
            ? new SupportChannelOptionDto(false, SupportChannelReasons.NoEmail, [])
            : new SupportChannelOptionDto(true, null, []);

        return new SupportReplyOptionsDto(
            thread,
            template,
            email,
            DefaultChannel(ticket.ReplyChannel, thread, template, email),
            new SupportChannelOptionDto(false, SupportChannelReasons.PortalUnavailable, []),
            await FormalAsync(ticket, messagingOn, now, cancellationToken));
    }

    // Fase 4b (H3, H5, H10): las mismas reglas que SendTicketReply. Fuera de la ventana el hilo sale por «Respuesta formal», que se aprueba
    // aparte de «Aviso de respuesta»; sin conversación, el selector trae las conexiones con esa plantilla.
    private async Task<SupportFormalOptionsDto> FormalAsync(
        SupportTicket ticket, bool messagingOn, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var blocked = await FormalBlockedReasonAsync(ticket, cancellationToken);
        if (!messagingOn)
        {
            return new SupportFormalOptionsDto(ticket.RequiresFormalResponse, blocked, SupportChannelReasons.MessagingDisabled, []);
        }

        var availability = await SupportReplyRules.ThreadAsync(channels, ticket, now, cancellationToken, formal: true);
        var connections = ticket.ConversationId is null && !string.IsNullOrWhiteSpace(ticket.Requester?.MobilePhone)
            ? (await channels.ListFormalTemplateConnectionsAsync(ticket.TenantId, cancellationToken))
                .Select(connection => new SupportConnectionOptionDto(connection.Id, connection.Name))
                .ToList()
            : [];
        return new SupportFormalOptionsDto(
            ticket.RequiresFormalResponse, blocked, availability.Route is null ? availability.Reason : null, connections);
    }

    // Quien firma va primero, como en el envío: sin responsable no hay carta, haya o no empresa.
    private async Task<string?> FormalBlockedReasonAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        if (ticket.AssigneeMemberId is null)
        {
            return SupportChannelReasons.SignerRequired;
        }

        return await letterhead.ExistsAsync(ticket.TenantId, ticket.FormVersionId, cancellationToken)
            ? null
            : SupportChannelReasons.OrganizationRequired;
    }

    private async Task<SupportChannelOptionDto> TemplateWithoutConversationAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ticket.Requester?.MobilePhone))
        {
            return new SupportChannelOptionDto(false, SupportChannelReasons.NoMobile, []);
        }

        var connections = await channels.ListTemplateConnectionsAsync(ticket.TenantId, cancellationToken);
        return new SupportChannelOptionDto(
            connections.Count > 0,
            connections.Count > 0 ? null : SupportChannelReasons.TemplateNotApproved,
            connections.Select(connection => new SupportConnectionOptionDto(connection.Id, connection.Name)).ToList());
    }

    private static string DefaultChannel(
        SupportReplyChannel preferred, SupportThreadOptionDto thread, SupportChannelOptionDto template, SupportChannelOptionDto email)
    {
        var available = new (SupportReplyChannel Channel, bool Available)[]
        {
            (SupportReplyChannel.Thread, thread.Available),
            (SupportReplyChannel.WhatsAppTemplate, template.Available),
            (SupportReplyChannel.Email, email.Available),
        };
        if (available.Any(option => option.Channel == preferred && option.Available))
        {
            return preferred.ToString();
        }

        return available.FirstOrDefault(option => option.Available) is { Available: true } first
            ? first.Channel.ToString()
            : preferred.ToString();
    }
}
