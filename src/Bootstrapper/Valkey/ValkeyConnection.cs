using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Bootstrapper.Valkey;

/// <summary>
/// El multiplexer de Valkey del proceso (spec 2026-11-13, D-1). <c>StackExchange.Redis</c> sólo lo referencia
/// Bootstrapper (P-1, <c>EphemeralStoreLayerTests</c>). Conecta en <see cref="StartAsync"/> con
/// <c>AbortOnConnectFail = false</c>: sin Valkey el host arranca igual, el cliente sigue reintentando y cada área usa su
/// respaldo local mientras tanto (<see cref="ValkeyGateway"/>).
///
/// **Ningún log lleva la cadena ni el host.** Los mensajes de las excepciones de StackExchange.Redis traen
/// <c>host:puerto</c>, y los argumentos de los eventos traen el <c>EndPoint</c>: acá sólo se loguean la clave, el
/// <c>FailureType</c> y el tipo de la excepción.
/// </summary>
internal sealed partial class ValkeyConnection(
    IConfiguration configuration,
    IOptions<ValkeyOptions> options,
    ILogger<ValkeyConnection> logger)
    : IHostedService, IAsyncDisposable, IDisposable
{
    /// <summary><c>ConnectionStrings:Valkey</c>, formato de StackExchange.Redis.</summary>
    internal const string ConnectionStringName = "Valkey";

    private const string ConnectionStringKey = "ConnectionStrings:" + ConnectionStringName;

    private ConnectionMultiplexer? _multiplexer;

    /// <summary><c>null</c>: apagado, sin cadena o todavía no conectó.</summary>
    public IConnectionMultiplexer? Multiplexer => Volatile.Read(ref _multiplexer);

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

    /// <summary>La cadena, o <c>null</c> si falta: vacía o con el token del pipeline sin resolver (<c>#{…}#</c>) cuenta como ausente.</summary>
    internal static string? ReadConnectionString(IConfiguration configuration)
    {
        var value = configuration.GetConnectionString(ConnectionStringName);
        return string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("#{", StringComparison.Ordinal)
            ? null
            : value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        var connectionString = ReadConnectionString(configuration);
        if (connectionString is null)
        {
            LogMissingConnectionString(logger, ConnectionStringKey);
            return;
        }

        ConnectionMultiplexer multiplexer;
        try
        {
            multiplexer = await ConnectionMultiplexer.ConnectAsync(BuildOptions(connectionString));
        }
        catch (Exception exception)
        {
            // Una cadena mal escrita: Parse lanza y el mensaje puede llevar parte del valor. Sólo el tipo.
            LogConnectFailed(logger, ConnectionStringKey, exception.GetType().Name);
            return;
        }

        multiplexer.ConnectionFailed += OnConnectionFailed;
        multiplexer.ConnectionRestored += OnConnectionRestored;
        Volatile.Write(ref _multiplexer, multiplexer);
        LogStarted(logger, ConnectionStringKey, multiplexer.IsConnected);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Detach() is { } multiplexer)
        {
            await multiplexer.DisposeAsync();
        }
    }

    // También síncrono: un ServiceProvider que se dispone con Dispose() lanza si un singleton sólo implementa
    // IAsyncDisposable (lo atrapa CompositionRootTests).
    public void Dispose() => Detach()?.Dispose();

    private ConnectionMultiplexer? Detach()
    {
        var multiplexer = Interlocked.Exchange(ref _multiplexer, null);
        if (multiplexer is not null)
        {
            multiplexer.ConnectionFailed -= OnConnectionFailed;
            multiplexer.ConnectionRestored -= OnConnectionRestored;
        }

        return multiplexer;
    }

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs args) =>
        LogConnectionFailed(logger, args.FailureType, args.ConnectionType);

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs args) =>
        LogConnectionRestored(logger, args.ConnectionType);

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Valkey client disabled (Valkey:Enabled = false).")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Valkey is enabled but {Key} is missing; every area uses its local fallback.")]
    private static partial void LogMissingConnectionString(ILogger logger, string key);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Valkey client could not start from {Key}: {ExceptionType}; every area uses its local fallback.")]
    private static partial void LogConnectFailed(ILogger logger, string key, string exceptionType);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Valkey client started from {Key}; connected: {Connected}.")]
    private static partial void LogStarted(ILogger logger, string key, bool connected);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Valkey connection failed ({FailureType}, {ConnectionType}); areas fall back until it is restored.")]
    private static partial void LogConnectionFailed(ILogger logger, ConnectionFailureType failureType, ConnectionType connectionType);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Valkey connection restored ({ConnectionType}).")]
    private static partial void LogConnectionRestored(ILogger logger, ConnectionType connectionType);
}
