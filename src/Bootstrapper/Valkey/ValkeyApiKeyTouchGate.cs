using Modules.ApiAccess.Application;
using StackExchange.Redis;

namespace Bootstrapper.Valkey;

/// <summary>
/// La compuerta del toque de <c>last_used_at</c> en Valkey (spec 2026-11-13, D-8): <c>SET qep:touch:apikey:{id} 1 NX EX 60</c>. Quien
/// la pone es el único request del minuto que va a Postgres, en todos los pods. Valkey sin responder deja pasar (<c>true</c>): el toque
/// vuelve a ser el de hoy, con el filtro de un minuto del repositorio.
/// </summary>
internal sealed class ValkeyApiKeyTouchGate(ValkeyGateway gateway) : IApiKeyTouchGate
{
    internal static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    internal static RedisKey KeyOf(Guid keyId) => $"{ValkeyKeys.Prefix}touch:apikey:{keyId:D}";

    public async Task<bool> ShouldTouchAsync(Guid keyId, CancellationToken cancellationToken)
    {
        var attempt = await gateway.TryAsync(
            ValkeyArea.ApiKeyTouch,
            database => database.StringSetAsync(KeyOf(keyId), 1, Ttl, When.NotExists),
            cancellationToken).ConfigureAwait(false);
        return !attempt.Succeeded || attempt.Value;
    }
}

/// <summary>El área apagada: cada request toca, como antes de Valkey.</summary>
internal sealed class NullApiKeyTouchGate : IApiKeyTouchGate
{
    public Task<bool> ShouldTouchAsync(Guid keyId, CancellationToken cancellationToken) => Task.FromResult(true);
}
