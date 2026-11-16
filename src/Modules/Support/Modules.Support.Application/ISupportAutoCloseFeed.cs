namespace Modules.Support.Application;

/// <summary>Fase 4b (H22): un ticket Resuelto que el worker revisa, con la hora de la última respuesta del cliente que sigue en el
/// expediente, si la hay.</summary>
public sealed record SupportAutoCloseCandidate(
    Guid TenantId, Guid TicketId, DateTimeOffset ResolvedAt, DateTimeOffset? LastCustomerReplyAt);

/// <summary>
/// Fase 4b (H22, owner 2026-11-16, Q-4): lo que el cierre automático lee. SQL crudo en Bootstrapper sobre <c>support.tickets</c>,
/// <c>support.ticket_messages</c>, <c>support.ticket_surveys</c> y <c>messaging.messages</c>, de todos los tenants.
///
/// «La última respuesta del cliente»: el <c>occurred_at</c> más reciente, posterior a la resolución, de un entrante en vivo (no
/// importado) vinculado al ticket cuyo vínculo está **ahora** <c>Linked</c> o <c>Pinned</c> (soltarlo lo saca), y que no es una respuesta
/// de la encuesta: ni el toque que cita una encuesta del ticket ni el mensaje que quedó como su calificación o su comentario.
/// </summary>
public interface ISupportAutoCloseFeed
{
    /// <summary>Los tickets Resueltos hace más de un día, del más viejo al más nuevo, después de <paramref name="after"/> (la última fila
    /// de la página anterior; null para la primera).</summary>
    Task<IReadOnlyList<SupportAutoCloseCandidate>> ListCandidatesAsync(
        int take, SupportAutoCloseCandidate? after, CancellationToken cancellationToken);

    /// <summary>La última respuesta del cliente del ticket posterior a <paramref name="after"/>; null si no hay.</summary>
    Task<DateTimeOffset?> LastCustomerReplyAsync(
        Guid tenantId, Guid ticketId, DateTimeOffset after, CancellationToken cancellationToken);
}
