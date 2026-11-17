namespace Modules.Support.Application;

/// <summary>Fase 4b (H18): el toque de un botón de la plantilla de la encuesta que cita su mensaje. <c>Text</c> es el texto del botón
/// (Meta lo devuelve como <c>button.text</c>, V-1); <c>OccurredAt</c>, la hora del toque según Meta.</summary>
public sealed record SupportSurveyTap(Guid TenantId, Guid SurveyId, Guid MessageId, string? Text, DateTimeOffset OccurredAt);

/// <summary>Fase 4b (H18): el primer texto del contacto después del toque, dentro de la ventana del comentario. <c>OccurredAt</c> es
/// cuándo lo escribió (la hora de Meta): la ventana se mide con ella y no con la hora en que el worker lo procesa (revisión del bloque D).</summary>
public sealed record SupportSurveyComment(Guid TenantId, Guid SurveyId, Guid MessageId, string Text, DateTimeOffset OccurredAt);

/// <summary>
/// Fase 4b (H18, owner 2026-11-16, Q-11): lo que el worker de la encuesta lee de Mensajería para las respuestas por WhatsApp. SQL crudo
/// en Bootstrapper sobre <c>support.ticket_surveys</c> y <c>messaging.messages</c>, de todos los tenants: Mensajería no avisa.
/// </summary>
public interface ISupportSurveyFeed
{
    /// <summary>Los toques de botón entrantes (no importados) que citan una encuesta mandada por WhatsApp, sin calificar y que todavía se
    /// responde (el ticket sigue en esa resolución), dentro de <c>Support:Workers:SurveyAnswerLookbackDays</c>. Por encuesta, en orden de
    /// llegada: el primero cuyo texto es un botón gana.</summary>
    Task<IReadOnlyList<SupportSurveyTap>> ListTapsAsync(int take, CancellationToken cancellationToken);

    /// <summary>Por encuesta calificada por WhatsApp y sin comentario: el primer texto entrante (no importado) de la conversación del
    /// toque, escrito después del toque y antes de que cierre la ventana. Una ventana ya cerrada se sigue mirando durante
    /// <c>Support:Workers:SurveyAnswerLookbackDays</c>, por si el texto llegó a tiempo y el worker iba tarde.</summary>
    Task<IReadOnlyList<SupportSurveyComment>> ListCommentsAsync(int take, CancellationToken cancellationToken);
}
