using BuildingBlocks.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Support.Application;

/// <summary>
/// Fase 4b, Tarea 11 (H16, H17, owner 2026-11-16, Q-2): manda la encuesta de satisfacción de cada resolución por el canal del ticket. Por
/// encuesta reclamada:
/// <list type="bullet">
/// <item>El tenant la tiene apagada: «sin encuesta» con <c>survey_disabled</c> (y esa encuesta no se responde).</item>
/// <item>Anónimo, o se responde por la página de consulta: queda publicada ahí (<c>Sent</c> por <c>Portal</c>, sin mensaje).</item>
/// <item>Conversación de WhatsApp: la plantilla de la encuesta con <c>ClientId</c> = la encuesta. Sin Mensajería, sin la plantilla aprobada
/// en la conexión de la conversación, sin autor o con el contacto bloqueado: «sin encuesta» con el motivo.</item>
/// <item>Por correo (o un ticket de Instagram, que nunca la recibe por Instagram) con correo en la foto: el correo con el enlace de
/// calificación; sólo el hash del token queda en la fila.</item>
/// <item>Cualquier otro: la página de consulta.</item>
/// </list>
/// <c>send_failed</c> y <c>connection_unavailable</c> se reintentan (1, 5 y 15 minutos) y después quedan <c>Failed</c>. Cada resultado se
/// guarda con su auditoría de sistema (sólo el nombre del campo) y después del commit avisa el ticket por el hub. La encuesta no sube la
/// versión del ticket.
/// </summary>
public sealed class SupportSurveySender(
    ISupportSurveyRepository surveys,
    ISupportTicketRepository tickets,
    ISupportSettingsRepository settings,
    ITenantModules tenantModules,
    ITenantDirectory tenantDirectory,
    ISupportReplyChannels channels,
    ISupportSurveyEmail email,
    ISupportPublicOptions publicOptions,
    ISupportCustomerLookup customers,
    SupportSystemAuthor author,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    IClock clock)
{
    /// <summary>Revisión del bloque C: lotes chicos. Cada encuesta renueva su reclamo antes de mandarse, pero un lote corto también
    /// acorta lo que espera la última.</summary>
    public const int DefaultBatchSize = 5;

    /// <summary>Cuánto dura el reclamo de una encuesta: lo que tarda un envío, con margen. Cubre un envío, no el lote: cada encuesta lo
    /// renueva justo antes de mandarse (<see cref="ProcessAsync"/>).</summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    private static readonly string[] SurveyFields = ["survey"];

    /// <summary>Reclama hasta <paramref name="take"/> encuestas que tocan. Cada id se procesa después en su propio scope
    /// (<see cref="ProcessAsync"/>) con el <see cref="SupportSurveyBatch.ClaimedUntil"/> del lote, así una que falla no arrastra a las
    /// demás.</summary>
    public async Task<SupportSurveyBatch> ClaimAsync(int take, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var claimedUntil = ToMicroseconds(now + ClaimLease);
        return new SupportSurveyBatch(await surveys.ClaimAsync(take, now, claimedUntil, cancellationToken), claimedUntil);
    }

    /// <summary>
    /// Una encuesta reclamada: renueva su reclamo, decide el canal, la manda y guarda cómo terminó. Revisión del bloque C: el reclamo del
    /// lote cubre un envío, no el lote entero, así que antes de mandar se renueva sólo si sigue siendo el de este lote
    /// (<paramref name="claimedUntil"/>); si venció y otro worker la volvió a reclamar, ésta no se toca y la manda el otro. Una que ya no
    /// está <c>Pending</c> tampoco se toca.
    /// </summary>
    public async Task ProcessAsync(Guid surveyId, DateTimeOffset claimedUntil, CancellationToken cancellationToken)
    {
        var renewedUntil = ToMicroseconds(clock.UtcNow + ClaimLease);
        if (!await surveys.RenewClaimAsync(surveyId, claimedUntil, renewedUntil, cancellationToken)
            || await surveys.FindAsync(surveyId, cancellationToken) is not { Status: SupportSurveyStatus.Pending } survey)
        {
            return;
        }

        var ticket = await tickets.FindAsync(survey.TenantId, survey.TicketId, cancellationToken)
            ?? throw new InvalidOperationException($"The ticket of survey '{survey.Id}' was not found.");
        await DecideAsync(survey, ticket, await settings.FindAsync(survey.TenantId, cancellationToken), cancellationToken);
        await SaveAsync(survey, cancellationToken);
    }

    /// <summary>
    /// Revisión del bloque C: <see cref="ProcessAsync"/> lanzó algo inesperado con esta encuesta. Se llama en un scope nuevo (el del intento
    /// fallido puede tener cambios a medias) y la pasa a reintento con <c>send_failed</c>: 1, 5 y 15 minutos y después <c>Failed</c>, como
    /// un envío que el canal rechazó. Sin esto quedaría reclamada y volvería a lanzar cada vez que vence el reclamo, para siempre. Una que ya
    /// no está <c>Pending</c> (el intento sí alcanzó a guardar) no se toca.
    /// </summary>
    public async Task RetryAfterFailureAsync(Guid surveyId, CancellationToken cancellationToken)
    {
        if (await surveys.FindAsync(surveyId, cancellationToken) is not { Status: SupportSurveyStatus.Pending } survey)
        {
            return;
        }

        survey.Retry(SupportChannelReasons.SendFailed, clock.UtcNow);
        await SaveAsync(survey, cancellationToken);
    }

    // La auditoría de sistema del resultado (sólo el nombre del campo), el guardado y, después del commit, el aviso del ticket por el hub.
    private async Task SaveAsync(SupportTicketSurvey survey, CancellationToken cancellationToken)
    {
        var ticketId = survey.TicketId;
        var action = survey.Status switch
        {
            SupportSurveyStatus.Sent => SupportAuditActions.TicketSurveySent,
            SupportSurveyStatus.Skipped => SupportAuditActions.TicketSurveySkipped,
            SupportSurveyStatus.Failed => SupportAuditActions.TicketSurveyFailed,
            _ => null,
        };
        if (action is not null)
        {
            auditPublisher.PublishSystem(
                survey.TenantId, action, SupportAuditActions.Resources.Ticket, ticketId.ToString(), SurveyFields, clock.UtcNow);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        // Plan N13: después del commit, también un reintento (la pantalla muestra el estado de la encuesta).
        await realtime.TicketChangedAsync(survey.TenantId, ticketId.Value, CancellationToken.None);
    }

    /// <summary>La decisión y el envío, sin guardar: deja la encuesta <c>Sent</c>, <c>Skipped</c> o con su reintento.</summary>
    internal async Task DecideAsync(
        SupportTicketSurvey survey, SupportTicket ticket, SupportTenantSettings? tenantSettings, CancellationToken cancellationToken)
    {
        if (tenantSettings is not { SurveyEnabled: true })
        {
            survey.Skip(SupportChannelReasons.SurveyDisabled, null, clock.UtcNow);
            return;
        }

        if (ticket.IsAnonymous || ticket.ReplyChannel is SupportReplyChannel.Portal)
        {
            survey.MarkSent(SupportSurveyChannel.Portal, null, null, clock.UtcNow);
            return;
        }

        if (ticket is { ConversationId: { } conversationId, ConversationChannel: SupportConversationRef.WhatsApp })
        {
            await WhatsAppAsync(survey, ticket, conversationId, cancellationToken);
            return;
        }

        // Q-2: Instagram nunca recibe la encuesta por Instagram; con correo en la foto, el enlace por correo.
        if ((ticket.ReplyChannel is SupportReplyChannel.Email || ticket.ConversationChannel == SupportConversationRef.Instagram)
            && ticket.Requester?.Email is { } address
            && !string.IsNullOrWhiteSpace(address))
        {
            await EmailAsync(survey, ticket, address.Trim(), cancellationToken);
            return;
        }

        survey.MarkSent(SupportSurveyChannel.Portal, null, null, clock.UtcNow);
    }

    private async Task WhatsAppAsync(
        SupportTicketSurvey survey, SupportTicket ticket, Guid conversationId, CancellationToken cancellationToken)
    {
        if (!await SupportConversationRules.IsEnabledAsync(tenantModules, ticket.TenantId, TenantModuleKeys.Messaging, cancellationToken))
        {
            survey.Skip(SupportChannelReasons.MessagingDisabled, SupportSurveyChannel.WhatsApp, clock.UtcNow);
            return;
        }

        if (await channels.FindThreadAsync(ticket.TenantId, conversationId, cancellationToken) is not { } thread)
        {
            survey.Skip(SupportChannelReasons.NoConversation, SupportSurveyChannel.WhatsApp, clock.UtcNow);
            return;
        }

        if (!await channels.IsSurveyTemplateApprovedAsync(ticket.TenantId, thread.ConnectionId, cancellationToken))
        {
            survey.Skip(SupportChannelReasons.TemplateNotApproved, SupportSurveyChannel.WhatsApp, clock.UtcNow);
            return;
        }

        if (await author.AuthorAsync(ticket, cancellationToken) is not { } signer)
        {
            survey.Skip(SupportChannelReasons.NoAuthor, SupportSurveyChannel.WhatsApp, clock.UtcNow);
            return;
        }

        var result = await channels.SendSurveyTemplateAsync(
            new SupportChannelSurveyTemplateSend(
                ticket.TenantId, conversationId, survey.Id, signer, await RecipientNameAsync(ticket, cancellationToken),
                ticket.DisplayNumber),
            cancellationToken);
        var now = clock.UtcNow;
        switch (result)
        {
            case SupportChannelSendResult.Sent sent:
                survey.MarkSent(SupportSurveyChannel.WhatsApp, sent.MessageId, null, now);
                break;
            case SupportChannelSendResult.Rejected { Reason: SupportChannelReasons.SendFailed or SupportChannelReasons.ConnectionUnavailable } retry:
                survey.Retry(retry.Reason, now);
                break;
            case SupportChannelSendResult.Rejected rejected:
                // Un motivo del canal (contacto bloqueado, plantilla retirada, nombre inválido): «sin encuesta», no una falla.
                survey.Skip(rejected.Reason, SupportSurveyChannel.WhatsApp, now);
                break;
            case SupportChannelSendResult.Failed failed:
                // Meta lo rechazó: el reintento usa el mismo ClientId y Mensajería reutiliza su fila Failed.
                survey.Retry(failed.Code, now);
                break;
            default:
                survey.Retry(SupportChannelReasons.SendFailed, now);
                break;
        }
    }

    private async Task EmailAsync(SupportTicketSurvey survey, SupportTicket ticket, string address, CancellationToken cancellationToken)
    {
        var tenantId = new TenantId(ticket.TenantId);
        var slug = await tenantDirectory.GetSlugAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"Tenant '{ticket.TenantId}' has no slug for the rating link.");
        var token = SupportSurveyTokens.NewToken();
        var result = await email.SendAsync(
            new SupportSurveyEmail(
                address,
                await tenantDirectory.GetDisplayNameAsync(tenantId, cancellationToken) ?? string.Empty,
                ticket.DisplayNumber,
                ticket.Subject,
                SupportSurveyTokens.RatingUrl(publicOptions, slug, token)),
            cancellationToken);
        var now = clock.UtcNow;
        switch (result)
        {
            case SupportChannelSendResult.Sent:
                // H19: sólo el hash. Un reintento emite otro token: el del correo que falló nunca sirvió.
                survey.MarkSent(SupportSurveyChannel.Email, null, SupportSurveyTokens.HashOf(token), now);
                break;
            case SupportChannelSendResult.Failed failed:
                survey.Retry(failed.Code, now);
                break;
            case SupportChannelSendResult.Rejected rejected:
                survey.Retry(rejected.Reason, now);
                break;
            default:
                survey.Retry(SupportChannelReasons.SendFailed, now);
                break;
        }
    }

    // Postgres guarda microsegundos: el reclamo que se compara al renovar tiene que ser el mismo valor que quedó en la columna.
    private static DateTimeOffset ToMicroseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);
    }

    // El nombre de la foto y, si no tiene, el del cliente vinculado: llena «nombre_contacto» de la plantilla.
    private async Task<string> RecipientNameAsync(SupportTicket ticket, CancellationToken cancellationToken)
    {
        var fromRequester = ticket.Requester?.DisplayName();
        if (!string.IsNullOrWhiteSpace(fromRequester))
        {
            return fromRequester;
        }

        return ticket.CustomerId is { } customerId
            && await customers.FindAsync(ticket.TenantId, customerId, cancellationToken) is { } customer
                ? customer.Name
                : string.Empty;
    }
}

/// <summary>Las encuestas que reclamó un lote y hasta cuándo es suyo el reclamo: <see cref="SupportSurveySender.ProcessAsync"/> sólo
/// renueva un reclamo que todavía tenga este valor.</summary>
public sealed record SupportSurveyBatch(IReadOnlyList<Guid> SurveyIds, DateTimeOffset ClaimedUntil);
