using System.Threading.Channels;
using Modules.ApiAccess.Domain;

namespace Bootstrapper.ApiKeys;

/// <summary>
/// Cola acotada entre el middleware de la API externa y <see cref="ExternalApiCallLogWriter"/>. El middleware sólo encola: Kestrel
/// espera los callbacks de <c>OnCompleted</c> antes de leer la siguiente petición de la misma conexión, así que una escritura ahí
/// se le sumaría a la siguiente llamada del integrador (spec 2026-11-01 parte 1, anti-objetivo: el registro no demora un envío).
/// Llena, descarta la fila: perder un registro es preferible a frenar una respuesta.
/// </summary>
public sealed class ExternalApiCallLogChannel
{
    /// <summary>Filas pendientes que aguanta antes de descartar. Cada una pesa unos cientos de bytes: 10 000 es holgura de sobra
    /// para una base lenta, sin que una caída larga se coma la memoria.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly Channel<ApiKeyRequest> _channel;

    public ExternalApiCallLogChannel(int capacity = DefaultCapacity) =>
        _channel = Channel.CreateBounded<ApiKeyRequest>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public ChannelReader<ApiKeyRequest> Reader => _channel.Reader;

    /// <summary>Encola sin esperar. <c>false</c> si la cola está llena: la fila se descarta.</summary>
    public bool TryEnqueue(ApiKeyRequest row) => _channel.Writer.TryWrite(row);
}
