using Modules.Customers.Domain;

namespace Modules.Customers.Application;

// Todo metodo recibe tenantId primero: el filtro de tenant es parte de la consulta, nunca un
// argumento opcional que el llamador se pueda olvidar.
public interface ICustomerRepository
{
    /// <summary>
    /// Una pagina del listado y el total que la acompana. El total viaja aparte porque la UI
    /// pagina: sin el no puede dibujar cuantas paginas hay, y contarlo en el cliente exigiria
    /// traerse todo — que es lo contrario de paginar.
    ///
    /// <c>name</c>/<c>identificationNumber</c>/<c>cuc</c> son tres filtros independientes
    /// (CLI-FILTROS-01): cada uno filtra su propia columna con ILIKE, y se combinan con AND
    /// cuando el llamador manda mas de uno. <c>search</c> es el criterio OR original —sobre los
    /// mismos tres campos— que el combobox de clientes de <c>quotes</c> todavia necesita.
    /// </summary>
    Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? identificationNumber,
        string? cuc,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Un lote del padron para exportar, con los mismos filtros que <see cref="SearchAsync"/> pero
    /// sin su tope de pagina: aquel existe para acotar una respuesta HTTP, y este camino escribe un
    /// archivo. El llamador recorre con <c>skip</c>/<c>take</c> hasta recibir menos de los que
    /// pidio, y es quien pone el limite de cuantas filas acepta exportar.
    ///
    /// El orden es estable —por CUC, que es unico dentro del tenant— y no por relevancia: recorrer
    /// en lotes un orden que no desempata puede saltear o repetir filas entre consultas.
    /// </summary>
    Task<IReadOnlyList<Customer>> ListForExportAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? identificationNumber,
        string? cuc,
        int skip,
        int take,
        CancellationToken cancellationToken);

    Task<Customer?> FindAsync(
        Guid tenantId,
        CustomerId customerId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Si algun cliente del tenant referencia esta clasificacion. La usan
    /// <c>DeleteClientClassificationHandler</c>, <c>UpdateClientClassificationHandler</c> y
    /// <c>DeactivateClientClassificationHandler</c> para responder un 422 legible antes de mutar —
    /// mismo patron que <c>IProductRepository.AnyWithTaxRateAsync</c> en Catalog. Editar o
    /// inactivar una clasificacion en uso queda tan bloqueado como borrarla: el prefijo del CUC de
    /// un cliente se congela al asignarse (ver <c>Customer.Update</c>), asi que una clasificacion
    /// en uso que cambiara de prefijo, o que un cliente ya no pudiera reasignar por estar inactiva,
    /// dejaria esa asignacion en un estado que nadie pidio.
    /// </summary>
    Task<bool> AnyWithClassificationAsync(
        Guid tenantId,
        ClientClassificationId classificationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cuales de estas identificaciones ya existen en el tenant, en una sola consulta. La usa la
    /// importacion masiva (Fase 5) para el chequeo de duplicados **contra la base** de todas las
    /// filas que pasaron la validacion de campo y la deduplicacion dentro del archivo: una consulta
    /// batch en vez de una por fila, para que una carga de mil clientes no haga mil round-trips.
    /// </summary>
    Task<IReadOnlySet<(IdentificationType Type, string Number)>> FindExistingIdentificationsAsync(
        Guid tenantId,
        IReadOnlyCollection<(IdentificationType Type, string Number)> identifications,
        CancellationToken cancellationToken);

    void Add(Customer customer);
}
