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
}
