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
    /// </summary>
    Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<Customer?> FindAsync(
        Guid tenantId,
        CustomerId customerId,
        CancellationToken cancellationToken);

    void Add(Customer customer);
}
