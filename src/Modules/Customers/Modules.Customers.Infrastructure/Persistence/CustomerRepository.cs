using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

internal sealed class CustomerRepository(CustomersDbContext dbContext) : ICustomerRepository
{
    private const string LikeEscapeCharacter = "\\";

    // La barra va primero: escaparla despues convertiria en literal la barra que acaban de agregar
    // los otros dos reemplazos.
    private static string EscapeLikeWildcards(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    public async Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId);

        var term = search?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            // ILike es la coincidencia case-insensitive de Npgsql. Los comodines tienen que ser
            // solo los dos que pone esta linea: `%` y `_` son comodines de LIKE, asi que un termino
            // sin escapar los convierte en parte de la sintaxis. `?search=_` devolvia el catalogo
            // entero —coincide con cualquier caracter—, que es lo contrario de filtrar. Lo
            // encontro la revision de fiabilidad de CAT-02, sobre este mismo codigo.
            //
            // Busca por nombre, numero de identificacion y CUC, que es literalmente lo que dice el
            // placeholder de la caja del listado: "Buscar por nombre, identificación o CUC"
            // (customers-list-page.tsx).
            var pattern = $"%{EscapeLikeWildcards(term)}%";
            query = query.Where(customer =>
                EF.Functions.ILike(customer.Name, pattern, LikeEscapeCharacter) ||
                EF.Functions.ILike(customer.IdentificationNumber, pattern, LikeEscapeCharacter) ||
                EF.Functions.ILike(customer.Cuc, pattern, LikeEscapeCharacter));
        }

        // El total se cuenta sobre la consulta **ya filtrada** y antes de paginar: es cuantos
        // clientes coinciden con la busqueda, no cuantos tiene el tenant. Contar despues del Skip
        // devolveria como mucho pageSize y la UI dibujaria una sola pagina siempre.
        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(customer => customer.Name)
            .ThenBy(customer => customer.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    // Con tracking a proposito, a diferencia de SearchAsync: los llamadores de este mutan el
    // agregado y dependen de la unidad de trabajo para persistirlo.
    public Task<Customer?> FindAsync(
        Guid tenantId,
        CustomerId customerId,
        CancellationToken cancellationToken) =>
        dbContext.Customers.SingleOrDefaultAsync(
            customer => customer.TenantId == tenantId && customer.Id == customerId,
            cancellationToken);

    public Task<bool> AnyWithClassificationAsync(
        Guid tenantId,
        ClientClassificationId classificationId,
        CancellationToken cancellationToken) =>
        dbContext.Customers
            .AsNoTracking()
            .AnyAsync(
                customer =>
                    customer.TenantId == tenantId &&
                    customer.ClassificationId == classificationId,
                cancellationToken);

    public async Task<IReadOnlySet<(IdentificationType Type, string Number)>> FindExistingIdentificationsAsync(
        Guid tenantId,
        IReadOnlyCollection<(IdentificationType Type, string Number)> identifications,
        CancellationToken cancellationToken)
    {
        if (identifications.Count == 0)
        {
            return new HashSet<(IdentificationType, string)>();
        }

        // Postgres/EF no traduce un `Contains` sobre una lista de tuplas (tipo, numero) a una sola
        // condicion SQL razonable, asi que el filtro se hace en dos pasos: un WHERE amplio en la
        // base (tipo IN (...) Y numero IN (...), que usa el indice) y la comparacion exacta del
        // par en memoria sobre ese conjunto ya chico. Sigue siendo **una** consulta, no una por
        // identificacion.
        var types = identifications.Select(identification => identification.Type).Distinct().ToArray();
        var numbers = identifications.Select(identification => identification.Number).Distinct().ToArray();

        var candidates = await dbContext.Customers
            .AsNoTracking()
            .Where(customer =>
                customer.TenantId == tenantId &&
                types.Contains(customer.IdentificationType) &&
                numbers.Contains(customer.IdentificationNumber))
            .Select(customer => new { customer.IdentificationType, customer.IdentificationNumber })
            .ToListAsync(cancellationToken);

        var candidateSet = candidates
            .Select(candidate => (candidate.IdentificationType, candidate.IdentificationNumber))
            .ToHashSet();

        return identifications.Where(candidateSet.Contains).ToHashSet();
    }

    public void Add(Customer customer) => dbContext.Customers.Add(customer);
}
