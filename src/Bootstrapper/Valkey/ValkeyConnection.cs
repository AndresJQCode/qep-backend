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

    private readonly Lock _gate = new();
    private ConnectionMultiplexer? _multiplexer;
    private bool _disposed;

    /// <summary><c>null</c>: apagado, sin cadena o todavía no conectó.</summary>
    public IConnectionMultiplexer? Multiplexer => Volatile.Read(ref _multiplexer);

    /// <summary>
    /// Opciones que aguantan la ACL de producción del usuario <c>qep</c> (<c>~qep:* &amp;qep:* +@all -@dangerous
    /// -@scripting</c>, DP-V4). Sin <c>ConfigurationChannel</c> el cliente no se suscribe a
    /// <c>__Booksleeve_MasterChanged</c>, que queda fuera de <c>&amp;qep:*</c>; sin <c>TieBreaker</c> no lee la clave
    /// <c>__Booksleeve_TieBreak</c>, que queda fuera de <c>~qep:*</c>. <c>AbortOnConnectFail = false</c>: el host arranca
    /// aunque Valkey no esté, y el cliente sigue reintentando.
    ///
    /// <c>CONFIG</c> queda fuera del <c>CommandMap</c> y la ACL de <c>qep</c> lleva <c>+info</c> (2026-11-17). Los dos son
    /// <c>@dangerous</c> y el cliente los mandaba al conectar y en cada chequeo de configuración (<c>ConfigCheckSeconds</c>,
    /// 60 s); la ACL los rechazaba —el cliente seguía con sus valores por defecto— pero cada rechazo entraba a
    /// <c>ACL LOG</c> y dejaba <c>ValkeyACLDenied</c> disparada todo el tiempo, tapando los rechazos que sí importan.
    /// <c>CONFIG</c> se apaga sin consecuencias. <c>INFO</c> no: sin él, StackExchange.Redis detecta la réplica con un
    /// <c>SET &lt;guid&gt; "" PX 1</c> sobre una clave fuera de <c>~qep:*</c>, y el ruido sólo cambia de forma. Por eso el
    /// permiso. Lo amarra <c>ValkeyAclTests.TheClientSendsNothingTheAclDenies</c>.
    /// </summary>
    internal static ConfigurationOptions BuildOptions(string connectionString)
    {
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ConfigurationChannel = string.Empty;
        options.TieBreaker = string.Empty;
        options.CommandMap = CommandMap.Create(new HashSet<string> { "CONFIG" }, available: false);
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

        Task<ConnectionMultiplexer> connecting;
        try
        {
            connecting = ConnectionMultiplexer.ConnectAsync(BuildOptions(connectionString));
        }
        catch (Exception exception)
        {
            // Una cadena mal escrita: Parse lanza y el mensaje puede llevar parte del valor. Sólo el tipo.
            LogConnectFailed(logger, ConnectionStringKey, exception.GetType().Name);
            return;
        }

        // Se espera el connect, para que los primeros requests no corran sobre el respaldo de memoria, pero con tope: con
        // Valkey caído ConnectAsync tarda el connectTimeout por cada intento, y eso no puede retener el arranque del host.
        // Pasado el tope (o cancelado el arranque) el connect sigue en segundo plano y se instala cuando termine.
        try
        {
            Attach(await connecting.WaitAsync(StartupConnectTimeout, cancellationToken).ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            LogConnectPending(logger, ConnectionStringKey);
            _ = AttachWhenConnectedAsync(connecting);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = AttachWhenConnectedAsync(connecting);
            throw;
        }
        catch (Exception exception)
        {
            LogConnectFailed(logger, ConnectionStringKey, exception.GetType().Name);
        }
    }

    /// <summary>Lo que <see cref="StartAsync"/> espera el primer connect antes de dejar arrancar al host.</summary>
    internal static readonly TimeSpan StartupConnectTimeout = TimeSpan.FromSeconds(2);

    private async Task AttachWhenConnectedAsync(Task<ConnectionMultiplexer> connecting)
    {
        try
        {
            Attach(await connecting.ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            LogConnectFailed(logger, ConnectionStringKey, exception.GetType().Name);
        }
    }

    // Un connect que termina después de Dispose no se instala: se cierra, o quedaría un multiplexer vivo sin dueño.
    private void Attach(ConnectionMultiplexer multiplexer)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                multiplexer.ConnectionFailed += OnConnectionFailed;
                multiplexer.ConnectionRestored += OnConnectionRestored;
                Volatile.Write(ref _multiplexer, multiplexer);
                LogStarted(logger, ConnectionStringKey, multiplexer.IsConnected);
                return;
            }
        }

        multiplexer.Dispose();
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
        ConnectionMultiplexer? multiplexer;
        lock (_gate)
        {
            _disposed = true;
            multiplexer = Interlocked.Exchange(ref _multiplexer, null);
        }

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

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Valkey client from {Key} did not connect within the startup bound; it keeps connecting in the background and every area uses its local fallback meanwhile.")]
    private static partial void LogConnectPending(ILogger logger, string key);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Valkey connection restored ({ConnectionType}).")]
    private static partial void LogConnectionRestored(ILogger logger, ConnectionType connectionType);
}
