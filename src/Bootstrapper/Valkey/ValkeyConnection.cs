using StackExchange.Redis;

namespace Bootstrapper.Valkey;

/// <summary>
/// El multiplexer de Valkey del proceso (spec 2026-11-13, D-1). <c>StackExchange.Redis</c> sólo lo referencia
/// Bootstrapper (P-1, <c>EphemeralStoreLayerTests</c>).
/// </summary>
internal sealed partial class ValkeyConnection
{
    /// <summary><c>ConnectionStrings:Valkey</c>, formato de StackExchange.Redis.</summary>
    internal const string ConnectionStringName = "Valkey";

    /// <summary>
    /// Opciones que aguantan la ACL de producción del usuario <c>qep</c> (<c>~qep:* &amp;qep:* +@all -@dangerous
    /// -@scripting</c>, DP-V4). Sin <c>ConfigurationChannel</c> el cliente no se suscribe a
    /// <c>__Booksleeve_MasterChanged</c>, que queda fuera de <c>&amp;qep:*</c>; sin <c>TieBreaker</c> no lee la clave
    /// <c>__Booksleeve_TieBreak</c>, que queda fuera de <c>~qep:*</c>. <c>AbortOnConnectFail = false</c>: el host arranca
    /// aunque Valkey no esté, y el cliente sigue reintentando.
    /// </summary>
    internal static ConfigurationOptions BuildOptions(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConfigurationChannel = string.Empty;
        options.TieBreaker = string.Empty;
        return options;
    }
}
