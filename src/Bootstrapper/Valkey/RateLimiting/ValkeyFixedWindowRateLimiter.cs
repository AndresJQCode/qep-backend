using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace Bootstrapper.Valkey.RateLimiting;

/// <summary>
/// Una ventana fija contada en Valkey y compartida por todos los pods (spec 2026-11-13, D-3). La clave es
/// <c>qep:rl:{política}:{partición}:{inicioDeVentana}</c>, con el inicio alineado a la época Unix
/// (<c>floor(ahora / ventana) × ventana</c>, V6 del plan) para que dos pods corten en el mismo segundo. El contador es
/// <c>INCR</c> + <c>EXPIRE clave (ventana + 1) NX</c> dentro de un <c>MULTI</c>/<c>EXEC</c>: la ACL de producción quita
/// <c>@scripting</c>, así que no hay Lua (V1, DP-V4). La clave muere sola un segundo después de cerrar la ventana.
///
/// **Nunca falla abierto.** Si Valkey no responde —caído, lento o sin conexión—, la llamada usa un
/// <see cref="FixedWindowRateLimiter"/> de memoria con las mismas opciones, creado una sola vez por partición la primera vez
/// que hace falta. Es exactamente el límite de antes, por pod, mientras dure la caída; el gateway cuenta cada respaldo en
/// <c>qep.valkey.fallbacks{area="rate_limiting"}</c>. No hay cola: el permiso se concede o se niega en el acto, como con el
/// <c>QueueLimit = 0</c> de todas las políticas.
/// </summary>
internal sealed class ValkeyFixedWindowRateLimiter : RateLimiter
{
    private readonly ValkeyGateway _gateway;
    private readonly string _policy;
    private readonly string _partition;
    private readonly FixedWindowRateLimiterOptions _options;
    private readonly TimeProvider _time;
    private readonly long _windowSeconds;
    private readonly ValkeyRateLimitLease _acquired = new(isAcquired: true, retryAfter: null);
    private readonly ValkeyRateLimitLease _deferred = new(isAcquired: false, retryAfter: null);

    private long _lastAcquireTimestamp;
    private FixedWindowRateLimiter? _fallback;
    private int _disposed;

    public ValkeyFixedWindowRateLimiter(
        ValkeyGateway gateway, string policy, string partition, FixedWindowRateLimiterOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PermitLimit, 0, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Window, TimeSpan.FromSeconds(1), nameof(options));

        _gateway = gateway;
        _policy = policy;
        _partition = partition;
        _options = options;
        _time = time;
        _windowSeconds = (long)options.Window.TotalSeconds;
        _lastAcquireTimestamp = time.GetTimestamp();
    }

    /// <summary>
    /// Sin respaldo, el tiempo desde el último pedido: el estado vive en Valkey y el limitador particionado puede desalojar
    /// esta instancia sin perder nada. Con respaldo, <c>null</c> mientras el de memoria tenga permisos gastados —desalojarlo
    /// perdería esos conteos— y, si no, el menor de los dos (Review Focus 4).
    /// </summary>
    public override TimeSpan? IdleDuration
    {
        get
        {
            var sinceLastAcquire = _time.GetElapsedTime(Interlocked.Read(ref _lastAcquireTimestamp));
            if (Volatile.Read(ref _fallback) is not { } fallback)
            {
                return sinceLastAcquire;
            }

            if (fallback.IdleDuration is not { } fallbackIdle)
            {
                return null;
            }

            return fallbackIdle < sinceLastAcquire ? fallbackIdle : sinceLastAcquire;
        }
    }

    public override RateLimiterStatistics? GetStatistics() => Volatile.Read(ref _fallback)?.GetStatistics();

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _options.PermitLimit);
        Interlocked.Exchange(ref _lastAcquireTimestamp, _time.GetTimestamp());

        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var windowStart = now / _windowSeconds * _windowSeconds;
        var key = ValkeyKeys.RateLimit(_policy, _partition, windowStart);
        var expiry = TimeSpan.FromSeconds(_windowSeconds + 1);

        var attempt = await _gateway.TryAsync(
            ValkeyArea.RateLimiting,
            database => CountAsync(database, key, permitCount, expiry),
            cancellationToken).ConfigureAwait(false);

        if (!attempt.Succeeded)
        {
            return await Fallback().AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false);
        }

        // Con 0 permisos sólo se pregunta si queda cupo, como en FixedWindowRateLimiter.
        var acquired = permitCount == 0
            ? attempt.Value < _options.PermitLimit
            : attempt.Value <= _options.PermitLimit;
        return acquired
            ? _acquired
            : new ValkeyRateLimitLease(
                isAcquired: false,
                retryAfter: TimeSpan.FromSeconds(Math.Max(1, windowStart + _windowSeconds - now)));
    }

    /// <summary>
    /// El camino síncrono siempre se niega, sin contar nada ni metadata. <c>RateLimitingMiddleware</c> llama primero
    /// <c>AttemptAcquire</c> y sólo con un rechazo pasa a <c>AcquireAsync</c>: si esto concediera desde la memoria, como
    /// suponía D-3 («el middleware no lo usa»), ninguna política llegaría a Valkey. Negar es seguro: quien sólo use el camino
    /// síncrono queda limitado de más, nunca sin límite.
    /// </summary>
    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _deferred;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeFallback();
        }

        base.Dispose(disposing);
    }

    protected override ValueTask DisposeAsyncCore()
    {
        DisposeFallback();
        return base.DisposeAsyncCore();
    }

    // MULTI / INCRBY / EXPIRE … NX / EXEC: atómico entre pods y sin script. Con 1 permiso StackExchange.Redis manda INCR.
    private static async Task<long> CountAsync(IDatabase database, RedisKey key, int permitCount, TimeSpan expiry)
    {
        var transaction = database.CreateTransaction();
        var counter = transaction.StringIncrementAsync(key, permitCount);
        var expire = transaction.KeyExpireAsync(key, expiry, ExpireWhen.HasNoExpiry);
        var execute = transaction.ExecuteAsync();
        await Task.WhenAll(execute, counter, expire).ConfigureAwait(false);
        return await counter.ConfigureAwait(false);
    }

    private FixedWindowRateLimiter Fallback()
    {
        if (Volatile.Read(ref _fallback) is { } existing)
        {
            return existing;
        }

        // AutoReplenishment: el limitador particionado sólo repone a los ReplenishingRateLimiter que le devuelve la fábrica,
        // y éste vive adentro, así que se repone con su propio timer.
        var created = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = _options.PermitLimit,
            Window = _options.Window,
            QueueLimit = _options.QueueLimit,
            QueueProcessingOrder = _options.QueueProcessingOrder,
            AutoReplenishment = true,
        });
        var winner = Interlocked.CompareExchange(ref _fallback, created, null);
        if (winner is null)
        {
            return created;
        }

        created.Dispose();
        return winner;
    }

    private void DisposeFallback()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Exchange(ref _fallback, null)?.Dispose();
        }
    }
}
