using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Fase 4b (H16): la cola de las encuestas. El reclamo es de todos los tenants: lo hace el worker.</summary>
public interface ISupportSurveyRepository
{
    /// <summary>
    /// Reclama hasta <paramref name="take"/> encuestas <c>Pending</c> que ya tocan (<c>next_attempt_at</c> hasta <paramref name="now"/>) y
    /// que nadie tiene tomadas, con <c>FOR UPDATE SKIP LOCKED</c>, y les pone <c>locked_until</c> en <paramref name="lockedUntil"/> en su
    /// propia transacción ya confirmada: dos workers nunca toman la misma, y una que quedó tomada por un proceso que murió vuelve a la
    /// cola al vencer. Devuelve sus ids, de la que toca primero a la última: cada una se procesa en su propio scope.
    /// </summary>
    Task<IReadOnlyList<Guid>> ClaimAsync(int take, DateTimeOffset now, DateTimeOffset lockedUntil, CancellationToken cancellationToken);

    /// <summary>Revisión del bloque C: renueva el reclamo de una encuesta <c>Pending</c> hasta <paramref name="renewedUntil"/> sólo si
    /// todavía es el que puso este lote (<c>locked_until</c> = <paramref name="claimedUntil"/>), en un <c>UPDATE</c> condicionado que se
    /// confirma solo. Falso si el reclamo ya es de otro worker o la encuesta dejó de estar <c>Pending</c>.</summary>
    Task<bool> RenewClaimAsync(
        Guid surveyId, DateTimeOffset claimedUntil, DateTimeOffset renewedUntil, CancellationToken cancellationToken);

    /// <summary>Fase 4b (H21): toma la fila de la encuesta (<c>FOR UPDATE</c>) y la devuelve con tracking, leída después de tomarla.
    /// Exige una transacción abierta (<c>ISupportUnitOfWork.BeginTransactionAsync</c>): así la primera respuesta gana y la segunda ve la
    /// calificación ya guardada. Sin tenant: lo comprueba quien llama.</summary>
    Task<SupportTicketSurvey?> LockAsync(Guid surveyId, CancellationToken cancellationToken);

    /// <summary>Fase 4b (H18): las encuestas calificadas por WhatsApp cuyo mensaje que pide el comentario no se anotó (ni enviado ni con
    /// motivo) y cuya ventana sigue abierta en <paramref name="now"/>: un tick que murió entre el envío y la anotación.</summary>
    Task<IReadOnlyList<Guid>> ListPromptPendingAsync(int take, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Con tracking, para guardar cómo terminó. Sin tenant: el id sale del reclamo del worker.</summary>
    Task<SupportTicketSurvey?> FindAsync(Guid surveyId, CancellationToken cancellationToken);
}
