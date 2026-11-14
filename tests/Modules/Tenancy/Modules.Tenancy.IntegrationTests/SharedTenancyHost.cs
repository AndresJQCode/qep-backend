using Microsoft.AspNetCore.Mvc.Testing;
using static Modules.Tenancy.IntegrationTests.TenancyTestDatabase;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Un host y una base por clase de prueba, en vez de uno por prueba. Arrancar el host es lo caro (comprueba las
/// migraciones de todos los módulos y levanta los workers). Mismo criterio que <c>SharedQuotationsHost</c>.
///
/// Las pruebas de una clase corren de a una y cada una trabaja sobre sus propios datos (registra su tenant o
/// usa ids nuevos), así que compartir el host no las mezcla. Se queda con host propio
/// (<c>StartDatabaseAsync</c> + <c>new …Factory</c>) la prueba que pide otra configuración (reloj fijo, auth
/// real, orígenes, operador), la que mira un doble que anota llamadas, la que lee o cuenta algo de toda la base
/// (SQL sin tenant, vistas del operador, registro de tenants y usuarios globales) y las de migración y semillas.
///
/// Cada archivo trae su propia factoría con su configuración por defecto: <see cref="Factory{TFactory}"/> la
/// crea una sola vez, con la base de la clase, y la reusa en las pruebas siguientes.
/// </summary>
public sealed class SharedTenancyHost : IAsyncLifetime
{
    private readonly Lock _gate = new();
    private TestDatabase? _database;
    private WebApplicationFactory<Program>? _factory;

    internal TestDatabase Database =>
        _database ?? throw new InvalidOperationException("The shared host has not been initialized.");

    /// <summary>El host de la clase. La primera prueba que lo pide lo crea con <paramref name="create"/>; las
    /// siguientes reciben el mismo.</summary>
    internal TFactory Factory<TFactory>(Func<string, TFactory> create)
        where TFactory : WebApplicationFactory<Program>
    {
        ArgumentNullException.ThrowIfNull(create);
        lock (_gate)
        {
            _factory ??= create(Database.GetConnectionString());
            return (TFactory)_factory;
        }
    }

    public async ValueTask InitializeAsync() => _database = await StartDatabaseAsync();

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
