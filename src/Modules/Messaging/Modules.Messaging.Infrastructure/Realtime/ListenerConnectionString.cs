using Microsoft.Extensions.Configuration;

namespace Modules.Messaging.Infrastructure.Realtime;

/// <summary>
/// Qué conexión usa el listener de <c>LISTEN messaging_events</c>. <c>LISTEN</c> no sobrevive a PgBouncer en modo
/// <c>transaction</c> y no da error: la conexión queda "escuchando" y no llega nada. Por eso acepta una clave propia,
/// <c>ConnectionStrings:QepDatabaseListener</c>, que apunta directo a Postgres; si no está, ausente, vacía o con el token
/// del pipeline sin resolver (<c>#{…}#</c>), cae a <c>QepDatabase</c> y el comportamiento es el de siempre.
/// </summary>
internal static class ListenerConnectionString
{
    internal const string ListenerKey = "QepDatabaseListener";
    internal const string DefaultKey = "QepDatabase";

    /// <summary>Lo que deja el pipeline cuando una variable no existe: <c>#{NOMBRE}#</c> literal.</summary>
    private const string UnresolvedTokenPrefix = "#{";

    /// <summary>La cadena a usar y el NOMBRE de la clave de la que salió (nunca el valor: el nombre es lo único que se loguea).</summary>
    internal static (string ConnectionString, string KeyName) Resolve(IConfiguration configuration)
    {
        var listener = configuration.GetConnectionString(ListenerKey)?.Trim();
        if (!string.IsNullOrEmpty(listener) && !IsUnresolvedPipelineToken(listener))
        {
            return (listener, ListenerKey);
        }

        var shared = configuration.GetConnectionString(DefaultKey)
            ?? throw new InvalidOperationException("Connection string 'QepDatabase' is required.");
        return (shared, DefaultKey);
    }

    private static bool IsUnresolvedPipelineToken(string value) =>
        value.StartsWith(UnresolvedTokenPrefix, StringComparison.Ordinal)
        && value.EndsWith("}#", StringComparison.Ordinal);
}
