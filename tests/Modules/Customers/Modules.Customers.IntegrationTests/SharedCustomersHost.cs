namespace Modules.Customers.IntegrationTests;

/// <summary>
/// Un host y una base por clase de prueba, para las pruebas que no necesitan uno propio. Arrancar el host es lo caro
/// (migra los módulos y levanta los workers).
///
/// En este proyecto casi todas las pruebas escriben en el mismo tenant fijo (<see cref="CustomersApiHarness.TenantId"/>)
/// con los mismos datos por defecto (documento, clasificación «CLI», prefijos), y muchas cuentan o esperan una lista vacía:
/// compartir la base las haría chocar. Por eso sólo toman el host de la clase las que no dejan nada escrito que otra
/// pueda ver —un 403, un 404, un 422 que rechaza antes de guardar, una lectura del esquema o de la plantilla— y las que
/// escriben con ids propios y sólo miran sus filas. Se queda con host propio (<c>StartDatabaseAsync</c> +
/// <c>new QepApiFactory</c>) toda prueba que crea clientes o clasificaciones del tenant compartido, siembra el tenant, cuenta
/// sobre la tabla entera, pide otra configuración o dobles, o migra.
/// </summary>
public sealed class SharedCustomersHost : IAsyncLifetime
{
    private CustomersApiHarness.TestDatabase? _database;
    private CustomersApiHarness.QepApiFactory? _factory;

    internal CustomersApiHarness.TestDatabase Database =>
        _database ?? throw new InvalidOperationException("The shared host has not been initialized.");

    internal CustomersApiHarness.QepApiFactory Factory =>
        _factory ?? throw new InvalidOperationException("The shared host has not been initialized.");

    public async ValueTask InitializeAsync()
    {
        _database = await CustomersApiHarness.StartDatabaseAsync();
        _factory = new CustomersApiHarness.QepApiFactory(_database.GetConnectionString());
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
