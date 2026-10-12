namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-11 decisión 14 y §6.3 paso 3.4: un token bucket en memoria por conexión que mantiene el ritmo de la campaña
/// (por defecto 20 mensajes por segundo, un cuarto de los 80 mps de Meta, para dejar aire a la bandeja del mismo número). Capacidad =
/// el ritmo —el primer segundo sale de golpe— y de ahí en adelante uno cada <c>1/ritmo</c>. Quien llega con el cubo vacío reserva su
/// lugar (el saldo queda negativo) y espera lo que le toca, así varios envíos en paralelo se reparten el tiempo sin pisarse.
/// Singleton: el estado es del proceso, no de un scope. El reloj y la espera entran por el constructor para probarlo sin dormir.
/// </summary>
public sealed class CampaignRateLimiter
{
    private readonly int _perSecond;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Bucket> _buckets = [];

    public CampaignRateLimiter(int messagesPerSecond, TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(messagesPerSecond, 1);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(delay);
        _perSecond = messagesPerSecond;
        _time = time;
        _delay = delay;
    }

    public async Task WaitAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan wait;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (!_buckets.TryGetValue(connectionId, out var bucket))
            {
                bucket = new Bucket { Tokens = _perSecond, Timestamp = now };
                _buckets[connectionId] = bucket;
            }

            var elapsed = _time.GetElapsedTime(bucket.Timestamp, now).TotalSeconds;
            bucket.Tokens = Math.Min(_perSecond, bucket.Tokens + (elapsed * _perSecond));
            bucket.Timestamp = now;
            bucket.Tokens -= 1;
            wait = bucket.Tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-bucket.Tokens / _perSecond);
        }

        if (wait > TimeSpan.Zero)
        {
            await _delay(wait, cancellationToken);
        }
    }

    private sealed class Bucket
    {
        public double Tokens { get; set; }

        public long Timestamp { get; set; }
    }
}
