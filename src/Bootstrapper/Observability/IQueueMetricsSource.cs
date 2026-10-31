namespace Bootstrapper.Observability;

/// <summary>Profundidad y edad de una cola. <see cref="OldestAgeSeconds"/> es la de la pendiente más vieja que nadie ha intentado
/// (<c>attempts = 0</c>), <c>0</c> si no hay, y <c>null</c> si la cola no tiene columna de edad (<c>campaign</c>).</summary>
public sealed record QueueReading(string Queue, long Backlog, long Retrying, double? OldestAgeSeconds);

public interface IQueueMetricsSource
{
    /// <exception cref="ArgumentOutOfRangeException">La cola no está en <see cref="QueueNames.All"/>.</exception>
    Task<QueueReading> ReadAsync(string queue, CancellationToken cancellationToken);
}
