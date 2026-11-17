using StackExchange.Redis;

namespace Bootstrapper.Valkey;

/// <summary>
/// Quién muestrea las colas cuando hay varios pods (spec 2026-11-13, D-7; plan P14). Las seis lecturas de
/// <c>QueueSamplerWorker</c> miden la misma base desde cada pod: con un líder, sólo uno las hace por vuelta. Lo gobierna
/// el área <c>Doorbells</c>. Apagada, o con Valkey sin responder, todos lideran: cada pod muestrea, como hoy.
///
/// La clave es <c>qep:leader:queue-sampler</c> con <c>SET NX PX 15000</c>, el mismo intervalo del muestreador. El líder
/// la renueva en su vuelta siguiente (lee su propio valor y le vuelve a poner el TTL), para no perderla por llegar justo
/// cuando todavía existe; si muere, otro pod la toma a los 15 s. Sin Lua (ACL de producción): leer y renovar no son
/// atómicos, y en la carrera más fea dos pods muestrean la misma vuelta, que es lo de hoy. El valor lleva el nombre de la
/// máquina y un id de la instancia: dos procesos en la misma máquina no se confunden.
/// </summary>
internal sealed class QueueSamplerLeadership(ValkeyGateway gateway)
{
    internal static readonly RedisKey Key = ValkeyKeys.Prefix + "leader:queue-sampler";

    internal static readonly TimeSpan Ttl = TimeSpan.FromMilliseconds(15000);

    private readonly RedisValue _identity = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task<bool> TryLeadAsync(CancellationToken cancellationToken)
    {
        if (!gateway.IsEnabled(ValkeyArea.Doorbells))
        {
            return true;
        }

        var attempt = await gateway.TryAsync(ValkeyArea.Doorbells, LeadAsync, cancellationToken).ConfigureAwait(false);
        return !attempt.Succeeded || attempt.Value;
    }

    private async Task<bool> LeadAsync(IDatabase database)
    {
        if (await database.StringSetAsync(Key, _identity, Ttl, When.NotExists).ConfigureAwait(false))
        {
            return true;
        }

        if (await database.StringGetAsync(Key).ConfigureAwait(false) != _identity)
        {
            return false;
        }

        await database.KeyExpireAsync(Key, Ttl).ConfigureAwait(false);
        return true;
    }
}
