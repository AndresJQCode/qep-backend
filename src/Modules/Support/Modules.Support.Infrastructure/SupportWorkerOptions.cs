using Modules.Support.Application;

namespace Modules.Support.Infrastructure;

/// <summary>Soporte fase 2 (plan E2): el seguimiento del hilo, con defaults en código. <c>Support:Workers:Enabled</c> (verdadero por
/// defecto) lo apaga: las pruebas que llaman <see cref="SupportThreadFollower.ProcessBatchAsync(CancellationToken)"/> a mano lo usan.</summary>
public sealed class SupportWorkerOptions
{
    public const string SectionName = "Support:Workers";

    /// <summary>Cada cuánto se sondean los entrantes: la latencia de un vínculo es de segundos.</summary>
    public int FollowIntervalSeconds { get; set; } = 5;

    /// <summary>Entrantes por pasada (y conversaciones con pendientes sin clasificar).</summary>
    public int BatchSize { get; set; } = SupportThreadFollower.DefaultBatchSize;
}
