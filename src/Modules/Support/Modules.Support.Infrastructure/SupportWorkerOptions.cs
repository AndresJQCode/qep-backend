using Modules.Support.Application;

namespace Modules.Support.Infrastructure;

/// <summary>Soporte fase 2 (plan E2): el seguimiento del hilo, con defaults en código. <c>Support:Workers:Enabled</c> (verdadero por
/// defecto) lo apaga: las pruebas que llaman <see cref="SupportThreadFollower.ProcessBatchAsync(CancellationToken)"/> a mano lo usan.</summary>
public sealed class SupportWorkerOptions
{
    public const string SectionName = "Support:Workers";

    public const int DefaultFeedLookbackHours = 24;

    /// <summary>Cada cuánto se sondean los entrantes: la latencia de un vínculo es de segundos.</summary>
    public int FollowIntervalSeconds { get; set; } = 5;

    /// <summary>Entrantes por pasada (y conversaciones con pendientes sin clasificar).</summary>
    public int BatchSize { get; set; } = SupportThreadFollower.DefaultBatchSize;

    /// <summary>Revisión final (I2): cuánto hacia atrás mira el feed, por la hora de llegada (<c>created_at</c>) del mensaje y de la fila
    /// sin clasificar. Acota cada pasada a lo reciente. Un worker caído más que esto deja sin vincular los entrantes que llegaron
    /// mientras tanto (el asesor los fija a mano) y sin reasignar las filas sin clasificar más viejas (el asesor las asigna).</summary>
    public int FeedLookbackHours { get; set; } = DefaultFeedLookbackHours;

    /// <summary>Fase 4b (H16): cada cuánto mira la cola de encuestas cuando no hay timbres (con timbres, la red de seguridad de la cola).</summary>
    public int SurveyIntervalSeconds { get; set; } = 30;

    /// <summary>Fase 4b: encuestas por reclamo.</summary>
    public int SurveyBatchSize { get; set; } = SupportSurveySender.DefaultBatchSize;

    /// <summary>Fase 4b (H18): cuánto después del toque el siguiente texto del cliente es el comentario de la encuesta.</summary>
    public int SurveyCommentWindowHours { get; set; } = 24;

    /// <summary>Fase 4b (H18): cuánto hacia atrás mira el feed de respuestas de la encuesta por WhatsApp.</summary>
    public int SurveyAnswerLookbackDays { get; set; } = 14;

    /// <summary>Fase 4b (H22): cada cuánto corre el cierre automático de los tickets resueltos.</summary>
    public int AutoCloseIntervalMinutes { get; set; } = 15;

    /// <summary>Spec 2026-11-22 §7: cada cuánto busca tickets sin asignar que pasaron el umbral del aviso.</summary>
    public int UnassignedAlertIntervalMinutes { get; set; } = 5;
}
