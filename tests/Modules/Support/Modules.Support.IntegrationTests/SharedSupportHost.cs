using Testcontainers.PostgreSql;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Un host y una base por clase de prueba, en vez de uno por prueba (copia de <c>SharedQuotationsHost</c>). Arrancar el host es
/// lo caro: migra todos los módulos y levanta los workers.
///
/// Lo comparte sólo la prueba que siembra su propio tenant (<see cref="SupportApiHarness.NewTenantId"/>) y corre con la
/// configuración por defecto. Se queda con host propio (<c>StartDatabaseAsync</c> + <c>new QepApiFactory</c>) la que cambia
/// <c>ITenantClock</c> u otro servicio, la que cuenta o lee sobre toda la base (SQL sin tenant) y las de migración: una prueba
/// compartida que rompe la regla falla según el orden de las demás de su clase.
/// </summary>
public sealed class SharedSupportHost : IAsyncLifetime
{
    private PostgreSqlContainer? _database;
    private SupportApiHarness.QepApiFactory? _factory;

    /// <summary>El host de la clase. Soporte no tiene outbox, así que no hay punto de partida que marcar.</summary>
    internal Task<SupportApiHarness.QepApiFactory> StartTestAsync() =>
        Task.FromResult(_factory ?? throw new InvalidOperationException("The shared host has not been initialized."));

    public async ValueTask InitializeAsync()
    {
        _database = await SupportApiHarness.StartDatabaseAsync();
        _factory = new SupportApiHarness.QepApiFactory(_database.GetConnectionString());
    }

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
