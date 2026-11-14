using Microsoft.Extensions.Configuration;

namespace Bootstrapper.Startup;

/// <summary>
/// Qué conexión usa el candado de arranque (spec 2026-10-29, D-2). Un advisory lock de transacción no puede pasar por
/// PgBouncer en modo <c>transaction</c> con garantías, así que va directo a Postgres con la misma regla del listener de
/// Messaging: <c>ConnectionStrings:QepDatabaseListener</c> si existe, no está vacía (tras <c>Trim</c>) y no es el token
/// del pipeline sin resolver (<c>#{…}#</c>); si no, <c>QepDatabase</c>.
///
/// Es una copia de <c>ListenerConnectionString</c> porque esa es <c>internal</c> de Messaging y Bootstrapper no debe
/// abrirle sus internals. La amarra <c>StartupLockConnectionStringParityTests</c>, como
/// <c>ShopifyWebhookTopicsParityTests</c> amarra los topics de Shopify: si cambias una, cambia la otra.
/// </summary>
internal static class StartupLockConnectionString
{
    internal const string DirectKey = "QepDatabaseListener";
    internal const string DefaultKey = "QepDatabase";

    /// <summary>Lo que deja el pipeline cuando una variable no existe: <c>#{NOMBRE}#</c> literal.</summary>
    private const string UnresolvedTokenPrefix = "#{";

    /// <summary>La cadena a usar y el NOMBRE de la clave de la que salió (nunca el valor: el nombre es lo único que se loguea).</summary>
    internal static (string ConnectionString, string KeyName) Resolve(IConfiguration configuration)
    {
        var direct = configuration.GetConnectionString(DirectKey)?.Trim();
        if (!string.IsNullOrEmpty(direct) && !IsUnresolvedPipelineToken(direct))
        {
            return (direct, DirectKey);
        }

        var shared = configuration.GetConnectionString(DefaultKey)
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");
        return (shared, DefaultKey);
    }

    private static bool IsUnresolvedPipelineToken(string value) =>
        value.StartsWith(UnresolvedTokenPrefix, StringComparison.Ordinal)
        && value.EndsWith("}#", StringComparison.Ordinal);
}
