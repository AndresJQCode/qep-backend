namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Un host y una base por clase de prueba, en vez de uno por prueba. Arrancar el host es lo caro (migra 24
/// módulos y levanta los workers): con dieciséis pruebas en paralelo la máquina se iba en arranques.
///
/// Las pruebas de una clase corren de a una, y casi todas registran su propio tenant, así que compartir el host
/// no las mezcla. Se queda con host propio (<c>StartDatabaseAsync</c> + <c>new QepApiFactory</c>) la prueba que
/// pide otra configuración (reloj fijo, worker de exportaciones, enlaces públicos), la que mira un doble que
/// anota llamadas (PDF, WhatsApp, bucket público, interceptor de fallas), la que lee o reclama algo de toda la
/// base (la cola de exportaciones, conteos, SQL sin tenant) y las de migración y semillas.
///
/// <see cref="QuotationsApiHarness.OutboxMessagesAsync"/> descarta los eventos que ya estaban cuando empezó la
/// prueba (<see cref="StartTestAsync"/>), para que vea los mismos que con una base propia.
/// </summary>
public sealed class SharedQuotationsHost : IAsyncLifetime
{
    private QuotationsApiHarness.TestDatabase? _database;
    private QuotationsApiHarness.QepApiFactory? _factory;

    internal QuotationsApiHarness.TestDatabase Database =>
        _database ?? throw new InvalidOperationException("The shared host has not been initialized.");

    /// <summary>El host de la clase, con el outbox marcado en el punto de partida de esta prueba.</summary>
    internal async Task<QuotationsApiHarness.QepApiFactory> StartTestAsync()
    {
        var factory = _factory ?? throw new InvalidOperationException("The shared host has not been initialized.");
        factory.OutboxBaseline = null;
        factory.OutboxBaseline = await QuotationsApiHarness.OutboxIdsAsync(factory);
        return factory;
    }

    public async ValueTask InitializeAsync()
    {
        _database = await QuotationsApiHarness.StartDatabaseAsync();
        _factory = new QuotationsApiHarness.QepApiFactory(_database.GetConnectionString());
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
