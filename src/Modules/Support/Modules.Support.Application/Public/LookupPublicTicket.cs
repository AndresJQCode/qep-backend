using BuildingBlocks.Application;
using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>La consulta es un comando y no una lectura: un intento fallido se anota en la clave del radicado.</summary>
public sealed record LookupPublicTicketCommand(
    string? TenantSlug, string VisitorIp, string? Number, string? Pin, string? TurnstileToken)
    : ICommand<SupportPublicTicketDto>;

/// <summary>
/// Consultar un ticket con radicado y PIN (spec §9.4, plan F7 a F9 y F13). La verificación (Turnstile, tenant, radicado, clave, contador
/// y bloqueo) es la de <see cref="SupportPublicAccess"/>, la misma con la que se califica por la consulta (fase 4b, H20); lo que se
/// muestra lo arma <see cref="SupportPublicTicketView"/>.
/// </summary>
public sealed class LookupPublicTicketHandler(SupportPublicAccess access, SupportPublicTicketView view)
    : ICommandHandler<LookupPublicTicketCommand, SupportPublicTicketDto>
{
    /// <summary>H8: la vida de la URL firmada del PDF en la consulta pública.</summary>
    public static readonly TimeSpan DocumentLifetime = SupportPublicTicketView.DocumentLifetime;

    public async Task<SupportPublicTicketDto> HandleAsync(LookupPublicTicketCommand command, CancellationToken cancellationToken)
    {
        var granted = await access.VerifyAsync(
            command.TenantSlug, command.VisitorIp, command.Number, command.Pin, command.TurnstileToken, cancellationToken);
        return await view.ComposeAsync(granted.Tenant.TenantId, granted.Ticket, granted.Context, cancellationToken);
    }
}

/// <summary>
/// Lo que la consulta muestra de un ticket ya verificado (plan F13): el radicado, el tipo, el estado, los plazos, las respuestas que
/// salieron con el PDF de las formales (H13) y, desde la fase 4b (H20), si la encuesta de la última resolución se puede calificar.
/// </summary>
public sealed class SupportPublicTicketView(
    ISupportReplyRepository replies,
    ISupportDocumentStorage documents,
    ISupportTicketRepository tickets)
{
    /// <summary>H8: la vida de la URL firmada del PDF en la consulta pública.</summary>
    public static readonly TimeSpan DocumentLifetime = TimeSpan.FromMinutes(10);

    public async Task<SupportPublicTicketDto> ComposeAsync(
        Guid tenantId, SupportTicket ticket, SupportReadContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(context);
        var (dueState, _) = SupportDueStates.Evaluate(
            ticket.Status, ticket.DueOn, context.Today, context.Clock.Calendar, context.DueSoonBusinessDays);
        // La hora de envío es UpdatedAt (triage #6 del Bloque F): la respuesta no tiene columna propia y no hace falta. MarkSent es la
        // última escritura de una respuesta Sent (EnsurePending impide marcarla otra vez), y el borrado de clientes sólo pone message_id
        // en NULL sin tocar updated_at. Un camino nuevo que modifique una respuesta ya enviada tiene que sumar sent_at antes.
        // Fase 4b (H13): el PDF de una respuesta formal enviada, con una URL firmada de 10 minutos. Una que no salió no se muestra, y su
        // documento tampoco.
        var sentReplies = (await replies.ListAsync(tenantId, ticket.Id, cancellationToken))
            .Where(reply => reply.Status is SupportReplyStatus.Sent)
            .OrderBy(reply => reply.UpdatedAt)
            .ToList();
        var replyDocuments = sentReplies.Count == 0
            ? new Dictionary<Guid, SupportReplyDocument>()
            : (await replies.ListDocumentsAsync(tenantId, ticket.Id, cancellationToken)).ToDictionary(item => item.ReplyId);
        var sent = new List<SupportPublicReplyDto>(sentReplies.Count);
        foreach (var reply in sentReplies)
        {
            var document = replyDocuments.GetValueOrDefault(reply.Id);
            var url = document is null
                ? null
                : await documents.CreateDownloadUrlAsync(document.StorageKey, document.FileName, DocumentLifetime, cancellationToken);
            sent.Add(new SupportPublicReplyDto(
                reply.Text, reply.UpdatedAt, reply.Channel.ToString(), url?.AbsoluteUri, document?.FileName));
        }

        // H20: la encuesta de la última resolución; sin ninguna (nunca se resolvió), null.
        var survey = await tickets.FindLatestSurveyAsync(tenantId, ticket.Id, cancellationToken);
        return new SupportPublicTicketDto(
            ticket.DisplayNumber,
            ticket.TypeName,
            ticket.Status.ToString(),
            ticket.ReceivedAt,
            ticket.DueOn,
            dueState.ToString(),
            ticket.TermIsLegal,
            ticket.CurrentTermBusinessDays,
            ticket.IsExtended,
            sent,
            survey is null
                ? null
                : new SupportPublicSurveyStateDto(survey.IsAnswerable(ticket), survey.Rating?.ToString(), SupportPublicSurveyOptions.All));
    }
}
