using Microsoft.AspNetCore.Mvc.Testing;

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Un host y una base por clase de prueba, en vez de uno por prueba. Arrancar el host es lo caro (migra o comprueba
/// los 24 módulos y levanta los workers): con dieciséis pruebas en paralelo la máquina se iba en arranques. Mismo
/// criterio que SharedQuotationsHost.
///
/// Las pruebas de una clase corren de a una, y las que lo usan toman un tenant propio por prueba (las constantes
/// <c>TenantId</c> de esas clases son propiedades de instancia con un id nuevo, y xUnit crea una instancia por
/// prueba), así que compartir el host no las mezcla. Se queda con host propio (<c>StartDatabaseAsync</c> +
/// <c>new QepApiFactory</c>) la prueba que pide otra configuración (reloj fijo, doble del verificador de uso),
/// la que lee algo de toda la base (el outbox sin filtro de tenant, conteos de una tabla entera), la que escribe con
/// el contenedor de servicios y las de migración y semillas. La que lee por SQL filtrando por un id suyo (un producto,
/// una tasa, un archivo) sí comparte, con <see cref="Database"/>.
///
/// Cada archivo tiene su propia fábrica, así que la clase le dice cuál usar en <see cref="StartTestAsync{TFactory}"/>:
/// la primera prueba la crea y las siguientes reciben la misma.
/// </summary>
public sealed class SharedCatalogHost : IAsyncLifetime
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CatalogTestDatabase.TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;

    /// <summary>La base de la clase, para la prueba que la lee por SQL. Sólo filtrando por un id de esa prueba:
    /// las demás pruebas de la clase escriben en la misma base.</summary>
    internal CatalogTestDatabase.TestDatabase Database =>
        _database ?? throw new InvalidOperationException("The shared host has not been initialized.");

    /// <summary>El host de la clase, creado con <paramref name="create"/> la primera vez.</summary>
    internal async Task<TFactory> StartTestAsync<TFactory>(Func<string, TFactory> create)
        where TFactory : WebApplicationFactory<Program>
    {
        var database = _database ?? throw new InvalidOperationException("The shared host has not been initialized.");
        await _gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            _factory ??= create(database.GetConnectionString());
            return (TFactory)_factory;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask InitializeAsync() =>
        _database = await CatalogTestDatabase.StartDatabaseAsync();

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

        _gate.Dispose();
    }
}
