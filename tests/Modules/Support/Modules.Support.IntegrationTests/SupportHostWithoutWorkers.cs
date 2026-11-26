using Testcontainers.PostgreSql;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Un host y una base por clase, como <see cref="SharedSupportHost"/>, pero con el seguimiento del hilo apagado: la prueba siembra
/// entrantes y filas sin clasificar a mano y llama <c>SupportThreadFollower.ProcessBatchAsync</c> cuando la necesita, sin que el worker
/// gane la carrera por detrás. Cada prueba siembra su tenant y sólo afirma sobre él; una pasada del seguimiento también toca lo que
/// dejaron las pruebas anteriores de la clase (corren en serie), y eso no cambia lo que afirma ninguna.
/// </summary>
public sealed class SupportHostWithoutWorkers : IAsyncLifetime
{
    private PostgreSqlContainer? _database;
    private SupportApiHarness.QepApiFactory? _factory;

    internal SupportApiHarness.QepApiFactory Factory =>
        _factory ?? throw new InvalidOperationException("The host has not been initialized.");

    public async ValueTask InitializeAsync() => (_database, _factory) = await SupportApiHarness.OpenOwnHostAsync();

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}
