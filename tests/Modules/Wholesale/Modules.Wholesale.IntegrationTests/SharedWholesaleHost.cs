using Testcontainers.PostgreSql;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>
/// Un host y una base por clase de prueba, en vez de uno por prueba (copia de <c>SharedSupportHost</c>). Arrancar el host es lo
/// caro: migra todos los módulos y levanta los workers.
///
/// Lo comparte sólo la prueba que siembra su propio tenant (<see cref="WholesaleApiHarness.NewTenantAsync"/>) y corre con la
/// configuración por defecto. Se queda con host propio la que cambia un servicio o lee sobre toda la base (SQL sin tenant, como
/// la sonda de usuarios): una prueba compartida que rompe la regla falla según el orden de las demás de su clase.
/// </summary>
public sealed class SharedWholesaleHost : IAsyncLifetime
{
    private PostgreSqlContainer? _database;
    private WholesaleApiHarness.QepApiFactory? _factory;

    internal WholesaleApiHarness.QepApiFactory Factory =>
        _factory ?? throw new InvalidOperationException("The shared host has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        _database = await WholesaleApiHarness.StartDatabaseAsync();
        _factory = new WholesaleApiHarness.QepApiFactory(_database.GetConnectionString());
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
