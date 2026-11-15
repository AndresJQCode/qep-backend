using System.Collections.Concurrent;
using BuildingBlocks.Application;
using StackExchange.Redis;

namespace Bootstrapper.Valkey;

/// <summary>
/// Los timbres de las colas sobre pub/sub de Valkey (spec 2026-11-13, D-7; plan P8 y P9). <see cref="Ring"/> publica en
/// <c>qep:wake:{cola}</c> sin esperar. Cada cola tiene una sola suscripción por proceso, creada con el primer
/// <see cref="Listen"/> y reintentada en el siguiente <c>WaitAsync</c> si falló; su handler libera el semáforo de
/// <b>cada</b> oyente vivo de la cola (outbox tiene una decena de consumidores en el mismo proceso). El semáforo es
/// <c>(0, 1)</c>: muchos avisos cuentan como uno, y uno que llega mientras el worker drena no se pierde.
///
/// Sin conexión, o sin suscripción todavía, el oyente espera el intervalo de hoy (<c>poll</c>), no la red de seguridad.
/// StackExchange.Redis vuelve a suscribir solo al reconectar; los avisos de la ventana caída se pierden y los cubre
/// <c>poll</c>. Sólo <c>PUBLISH</c> y <c>SUBSCRIBE</c> (<c>@pubsub</c>), en canales literales cubiertos por <c>&amp;qep:*</c>.
/// </summary>
internal sealed class ValkeyQueueDoorbell(ValkeyGateway gateway) : IQueueDoorbell
{
    private readonly ValkeyGateway _gateway = gateway;
    private readonly ConcurrentDictionary<string, QueueState> _queues = new(StringComparer.Ordinal);

    public void Ring(string queue) => _gateway.Publish(ValkeyArea.Doorbells, ChannelOf(queue));

    public IDoorbellListener Listen(string queue)
    {
        var state = _queues.GetOrAdd(queue, static name => new QueueState(name));
        var listener = new Listener(this, state);
        state.Add(listener);
        _ = EnsureSubscribedAsync(state, CancellationToken.None);
        return listener;
    }

    /// <summary>Para las pruebas: la cola ya tiene su suscripción.</summary>
    internal bool IsSubscribed(string queue) => _queues.TryGetValue(queue, out var state) && state.Subscribed;

    internal static RedisChannel ChannelOf(string queue) => RedisChannel.Literal(ValkeyKeys.Prefix + "wake:" + queue);

    private async Task<bool> EnsureSubscribedAsync(QueueState state, CancellationToken cancellationToken)
    {
        if (state.Subscribed)
        {
            return true;
        }

        // Otro oyente ya está suscribiendo: este espera su poll y vuelve a mirar en la siguiente vuelta.
        if (!await state.Subscribing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            if (!state.Subscribed && await _gateway
                    .TrySubscribeAsync(ValkeyArea.Doorbells, ChannelOf(state.Queue), state.WakeAll, cancellationToken)
                    .ConfigureAwait(false))
            {
                state.Subscribed = true;
            }

            return state.Subscribed;
        }
        finally
        {
            state.Subscribing.Release();
        }
    }

    internal sealed class QueueState(string queue)
    {
        private readonly Lock _gate = new();
        private readonly List<Listener> _listeners = [];
        private volatile bool _subscribed;

        public string Queue { get; } = queue;

        public SemaphoreSlim Subscribing { get; } = new(1, 1);

        public bool Subscribed
        {
            get => _subscribed;
            set => _subscribed = value;
        }

        public void Add(Listener listener)
        {
            lock (_gate)
            {
                _listeners.Add(listener);
            }
        }

        public void Remove(Listener listener)
        {
            lock (_gate)
            {
                _listeners.Remove(listener);
            }
        }

        public void WakeAll()
        {
            Listener[] listeners;
            lock (_gate)
            {
                listeners = [.. _listeners];
            }

            foreach (var listener in listeners)
            {
                listener.Wake();
            }
        }
    }

    internal sealed class Listener : IDoorbellListener
    {
        private readonly ValkeyQueueDoorbell _owner;
        private readonly QueueState _state;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private int _disposed;

        internal Listener(ValkeyQueueDoorbell owner, QueueState state)
        {
            _owner = owner;
            _state = state;
        }

        internal string Queue => _state.Queue;

        /// <summary>Conectado y con la suscripción de la cola: sólo así un lote lleno repite sin esperar (fix round 2).</summary>
        public bool IsLive => _owner._gateway.IsConnected && _state.Subscribed;

        public async Task<bool> WaitAsync(TimeSpan poll, TimeSpan safetyNet, CancellationToken cancellationToken)
        {
            if (!_owner._gateway.IsConnected
                || !await _owner.EnsureSubscribedAsync(_state, cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
                return false;
            }

            return await _signal.WaitAsync(safetyNet, cancellationToken).ConfigureAwait(false);
        }

        internal void Wake()
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Ya tenía un aviso pendiente: muchos cuentan como uno.
            }
            catch (ObjectDisposedException)
            {
                // El oyente se soltó entre la foto de la lista y el aviso.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _state.Remove(this);
            _signal.Dispose();
        }
    }
}

/// <summary>
/// Los timbres con el área apagada (<c>Valkey:Areas:Doorbells = false</c>): <see cref="Ring"/> no hace nada y el oyente
/// espera el intervalo de hoy (plan P9). Todo apagado se comporta como antes.
/// </summary>
internal sealed class NullQueueDoorbell : IQueueDoorbell
{
    public void Ring(string queue)
    {
    }

    public IDoorbellListener Listen(string queue) => PollingListener.Instance;

    private sealed class PollingListener : IDoorbellListener
    {
        public static readonly PollingListener Instance = new();

        /// <summary>Nunca: con el área apagada todo espera el intervalo de hoy, también un lote lleno.</summary>
        public bool IsLive => false;

        public async Task<bool> WaitAsync(TimeSpan poll, TimeSpan safetyNet, CancellationToken cancellationToken)
        {
            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            return false;
        }

        public void Dispose()
        {
        }
    }
}
