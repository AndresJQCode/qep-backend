using BuildingBlocks.Application;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>
/// Fase 4b, Tarea 12 (H18, H21, owner 2026-11-16, Q-11): las respuestas de la encuesta por WhatsApp. Corre en el tick del worker de la
/// encuesta, después de los envíos, cada elemento en su propio scope:
/// <list type="bullet">
/// <item>Un toque que cita la encuesta y cuyo texto es uno de los tres botones la califica con ese mensaje (la primera gana, con la fila
/// tomada) y abre la ventana del comentario: la hora del toque más <c>Support:Workers:SurveyCommentWindowHours</c>. Un toque sobre una
/// encuesta que ya no se responde (reabierto, otra resolución) o con otro texto no hace nada.</item>
/// <item>Después pide el comentario una sola vez: un texto libre por el envío de respuestas de Soporte, con el <c>ClientId</c> fijo de
/// <see cref="SupportSurveyPrompt"/>. No es una respuesta del asesor: no toma, no asigna ni reabre la conversación. Se anota cómo terminó y
/// no se reintenta; si el proceso muere antes de anotarlo, el tick siguiente lo repite con el mismo <c>ClientId</c>.</item>
/// <item>El primer texto del contacto después del toque, dentro de la ventana, es el comentario.</item>
/// </list>
/// Cada cambio se guarda con su auditoría de sistema (sólo el campo) y después del commit avisa el ticket por el hub. No sube la versión
/// del ticket.
/// </summary>
public sealed class SupportSurveyAnswers(
    ISupportSurveyFeed feed,
    ISupportSurveyRepository surveys,
    ISupportTicketRepository tickets,
    ISupportReplyChannels channels,
    SupportSystemAuthor author,
    ISupportUnitOfWork unitOfWork,
    ISupportAuditPublisher auditPublisher,
    ISupportRealtime realtime,
    IClock clock)
{
    /// <summary>Toques, mensajes pendientes y comentarios por tick.</summary>
    public const int DefaultBatchSize = 50;

    private static readonly string[] SurveyFields = ["survey"];

    public Task<IReadOnlyList<SupportSurveyTap>> ListTapsAsync(int take, CancellationToken cancellationToken) =>
        feed.ListTapsAsync(take, cancellationToken);

    public Task<IReadOnlyList<SupportSurveyComment>> ListCommentsAsync(int take, CancellationToken cancellationToken) =>
        feed.ListCommentsAsync(take, cancellationToken);

    /// <summary>Las encuestas calificadas por WhatsApp cuyo mensaje que pide el comentario no se anotó y cuya ventana sigue abierta.</summary>
    public Task<IReadOnlyList<Guid>> ListPendingPromptsAsync(int take, CancellationToken cancellationToken) =>
        surveys.ListPromptPendingAsync(take, clock.UtcNow, cancellationToken);

    /// <summary>Califica la encuesta con el toque, si corresponde, y pide el comentario. Falso si el toque no calificó nada.</summary>
    public async Task<bool> RecordTapAsync(SupportSurveyTap tap, TimeSpan commentWindow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tap);
        if (SupportSurveyRatings.FromButtonText(tap.Text) is not { } rating)
        {
            return false;
        }

        SupportTicketSurvey survey;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // H21: con la fila tomada, la primera calificación gana aunque dos réplicas lean el mismo toque.
            if (await surveys.LockAsync(tap.SurveyId, cancellationToken) is not { } locked
                || locked.TenantId != tap.TenantId
                || await tickets.FindAsync(locked.TenantId, locked.TicketId, cancellationToken) is not { } ticket
                || !locked.IsAnswerable(ticket))
            {
                return false;
            }

            survey = locked;
            var now = clock.UtcNow;
            survey.Answer(
                ticket, rating, null, SupportSurveyChannel.WhatsApp, tap.MessageId, tap.OccurredAt + commentWindow, now);
            auditPublisher.PublishSystem(
                survey.TenantId, SupportAuditActions.TicketSurveyAnswered, SupportAuditActions.Resources.Ticket,
                survey.TicketId.ToString(), SurveyFields, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await realtime.TicketChangedAsync(survey.TenantId, survey.TicketId.Value, CancellationToken.None);
        await SendPromptAsync(survey.Id, cancellationToken);
        return true;
    }

    /// <summary>
    /// H18: el mensaje que pide el comentario, una vez por encuesta. Sólo si la encuesta se calificó por WhatsApp, el mensaje nunca se
    /// anotó y la ventana del comentario sigue abierta. Sin conversación o sin autor se anota el motivo sin mandar nada.
    /// </summary>
    public async Task SendPromptAsync(Guid surveyId, CancellationToken cancellationToken)
    {
        if (await surveys.FindAsync(surveyId, cancellationToken) is not
            {
                Rating: not null, AnsweredVia: SupportSurveyChannel.WhatsApp, PromptSentAt: null, PromptFailureCode: null,
            } survey
            || survey.CommentUntil is not { } until
            || until <= clock.UtcNow
            || await tickets.FindAsync(survey.TenantId, survey.TicketId, cancellationToken) is not { } ticket)
        {
            return;
        }

        var (messageId, failure) = await SendAsync(ticket, survey, cancellationToken);
        var now = clock.UtcNow;
        survey.RecordPrompt(messageId, failure, now);
        auditPublisher.PublishSystem(
            survey.TenantId, SupportAuditActions.TicketSurveyPrompted, SupportAuditActions.Resources.Ticket,
            survey.TicketId.ToString(), SurveyFields, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await realtime.TicketChangedAsync(survey.TenantId, survey.TicketId.Value, CancellationToken.None);
    }

    /// <summary>Guarda el comentario, si la encuesta todavía lo espera. Falso si no lo tomó.</summary>
    public async Task<bool> RecordCommentAsync(SupportSurveyComment comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(comment);
        SupportTicketSurvey survey;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var now = clock.UtcNow;
            if (await surveys.LockAsync(comment.SurveyId, cancellationToken) is not { } locked
                || locked.TenantId != comment.TenantId
                || !locked.AddComment(comment.Text, comment.MessageId, now))
            {
                return false;
            }

            survey = locked;
            auditPublisher.PublishSystem(
                survey.TenantId, SupportAuditActions.TicketSurveyCommented, SupportAuditActions.Resources.Ticket,
                survey.TicketId.ToString(), SurveyFields, now);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        await realtime.TicketChangedAsync(survey.TenantId, survey.TicketId.Value, CancellationToken.None);
        return true;
    }

    private async Task<(Guid? MessageId, string? Failure)> SendAsync(
        SupportTicket ticket, SupportTicketSurvey survey, CancellationToken cancellationToken)
    {
        if (ticket.ConversationId is not { } conversationId)
        {
            return (null, SupportChannelReasons.NoConversation);
        }

        // H17: el mismo autor que la plantilla de la encuesta.
        if (await author.AuthorAsync(ticket, cancellationToken) is not { } signer)
        {
            return (null, SupportChannelReasons.NoAuthor);
        }

        var result = await channels.SendThreadAsync(
            new SupportChannelThreadSend(
                ticket.TenantId, conversationId, SupportSurveyPrompt.ClientIdFor(survey.Id), signer, SupportSurveyPrompt.Text, UploadId: null),
            cancellationToken);
        return result switch
        {
            SupportChannelSendResult.Sent { MessageId: { } messageId } => (messageId, null),
            SupportChannelSendResult.Rejected rejected => (null, rejected.Reason),
            SupportChannelSendResult.Failed failed => (null, failed.Code),
            _ => (null, SupportChannelReasons.SendFailed),
        };
    }
}
