using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Bootstrapper.Valkey;

/// <summary>
/// Las claves de Valkey: <c>qep:{área}:{…}</c>, en minúsculas y con GUID en formato <c>D</c>. El prefijo no es estético:
/// la ACL de producción del usuario <c>qep</c> sólo deja tocar <c>~qep:*</c> (DP-V4).
/// </summary>
internal static class ValkeyKeys
{
    public const string Prefix = "qep:";

    public static RedisKey Presence(Guid tenantId) => $"{Prefix}presence:{tenantId:D}";

    /// <summary>Spec 2026-11-15 §5.3: who is composing on one topic of the app hub (a sorted set per connection).</summary>
    public static RedisKey RealtimePresence(Guid tenantId, string topic) => $"{Prefix}rt:presence:{tenantId:D}:{topic}";

    /// <summary>Spec 2026-11-22 D-1: the members of one tenant that have QEP open (the app hub), as a sorted set scored by heartbeat time.
    /// Not <see cref="Presence"/>: that one is the inbox's «Conectados».</summary>
    public static RedisKey AppOnline(Guid tenantId) => $"{Prefix}rt:online:{tenantId:D}";

    public static RedisKey RateLimit(string policy, string partition, long windowStartUnixSeconds) =>
        $"{Prefix}rl:{policy}:{partition}:{windowStartUnixSeconds}".ToLowerInvariant();

    /// <summary>El contador de versión de la caché de valores legales (spec 2026-11-22, «Caché»): quien escribe lo sube después del commit.</summary>
    public static RedisKey LegalParametersVersion => $"{Prefix}legalparams:ver";

    /// <summary>La tabla entera de valores legales de una versión: el lector lee la versión antes de cargar y guarda bajo ésa.</summary>
    public static RedisKey LegalParametersTable(long version) => $"{Prefix}legalparams:{version}";

    /// <summary>16 hex en minúsculas de SHA-256 (UTF-8): acorta una partición larga (patrón de ruta + IP) sin perder la unicidad práctica.</summary>
    public static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
