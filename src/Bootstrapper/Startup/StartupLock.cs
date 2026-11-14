using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bootstrapper.Startup;

/// <summary>
/// Spec 2026-10-29 (réplicas múltiples), D-2: el semáforo que impide que dos pods migren o siembren a la vez. Abre una
/// conexión propia, directa a Postgres y sin pool (<see cref="StartupLockConnectionString"/>), abre una transacción y toma
/// <c>pg_advisory_xact_lock(Namespace, Key)</c>. El trabajo corre en sus propias conexiones del pool, como siempre: el
/// candado no es una unidad de trabajo. Al terminar, bien o mal, la transacción se deshace (no escribió nada) y la conexión
/// se cierra, y eso suelta el candado.
///
/// Es de transacción y no de sesión a propósito: no hay <c>pg_advisory_unlock</c> que olvidar, y si el pod muere a mitad
/// del bloque, Postgres suelta el candado al cerrarse la conexión.
/// </summary>
public static partial class StartupLock
{
    /// <summary>
    /// Primera clave de la forma de dos: la fecha del spec revisado. Cualquier constante sirve mientras ningún otro
    /// candado de dos claves la use: hoy están <c>TenantChangeLock.Namespace</c> = 20261008,
    /// <c>AutomationsConversationLock.Namespace</c> = 20261022 y <c>ShopifyTokenLock.Namespace</c> = 20261112.
    /// </summary>
    public const int Namespace = 20261113;

    public const int Key = 1;

    [LoggerMessage(EventId = 4108, Level = LogLevel.Information,
        Message = "Startup lock acquired after {WaitMilliseconds} ms using connection string key {ConnectionStringKey}.")]
    private static partial void LogAcquired(ILogger logger, long waitMilliseconds, string connectionStringKey);

    [LoggerMessage(EventId = 4109, Level = LogLevel.Information,
        Message = "Startup migrations and seed finished in {ElapsedMilliseconds} ms under the startup lock.")]
    private static partial void LogFinished(ILogger logger, long elapsedMilliseconds);

    /// <summary>
    /// Lo que llama <c>Program.cs</c>: resuelve la conexión del candado como la del listener de Messaging y corre
    /// <paramref name="work"/> con el candado tomado. La espera respeta <paramref name="cancellationToken"/>.
    /// </summary>
    public static Task RunUnderStartupLockAsync(
        this IServiceProvider services,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(work);

        var configuration = services.GetRequiredService<IConfiguration>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StartupLock).FullName!);
        var (connectionString, keyName) = StartupLockConnectionString.Resolve(configuration);

        return RunAsync(connectionString, keyName, logger, token => work(services, token), cancellationToken);
    }

    /// <summary>El núcleo, separado de la configuración para que las pruebas le pasen la cadena directo.</summary>
    internal static async Task RunAsync(
        string connectionString,
        string keyName,
        ILogger logger,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        // Sin pool: la conexión es de un solo uso, y al cerrarla se cierra de verdad. Una del pool volvería al pool con
        // la sesión viva.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var waiting = Stopwatch.StartNew();

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // La transacción queda ociosa todo el bloque, y el segundo pod espera lo que tarde el primero en migrar. Un
            // idle_in_transaction_session_timeout del servidor mataría la conexión y soltaría el candado a mitad de una
            // migración; un lock_timeout o un statement_timeout cortarían la espera. SET LOCAL los apaga sólo acá.
            await using (var settings = new NpgsqlCommand(
                "SET LOCAL lock_timeout = 0; SET LOCAL statement_timeout = 0; SET LOCAL idle_in_transaction_session_timeout = 0",
                connection,
                transaction))
            {
                await settings.ExecuteNonQueryAsync(cancellationToken);
            }

            // CommandTimeout = 0: el de Npgsql es 30 s por defecto y cortaría la espera del segundo pod justo mientras el
            // primero migra. La espera la acota el cancellationToken (el apagado del host) y el startupProbe.
            await using (var acquire = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(@namespace, @key)", connection, transaction) { CommandTimeout = 0 })
            {
                acquire.Parameters.AddWithValue("namespace", Namespace);
                acquire.Parameters.AddWithValue("key", Key);
                await acquire.ExecuteNonQueryAsync(cancellationToken);
            }

            LogAcquired(logger, waiting.ElapsedMilliseconds, keyName);

            var running = Stopwatch.StartNew();
            await work(cancellationToken);
            LogFinished(logger, running.ElapsedMilliseconds);
        }
        finally
        {
            await ReleaseAsync(transaction);
        }
    }

    /// <summary>
    /// Deshace la transacción (no escribió nada). Si la conexión ya está rota —una espera cancelada, un servidor que la
    /// cortó—, el rollback puede fallar; no se deja que tape la excepción original: cerrar la conexión, que es sin pool,
    /// suelta el candado igual.
    /// </summary>
    private static async Task ReleaseAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // El dispose de la conexión, que sigue a esto, cierra la sesión y con ella el candado.
        }
    }
}
