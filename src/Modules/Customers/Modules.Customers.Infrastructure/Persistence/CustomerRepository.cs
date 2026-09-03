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

    // `null` para un filtro vacio/ausente, para que el llamador sepa si tiene que agregar el
    // `Where` o no — un patron `"%%"` matchearia todo, que no es lo mismo que "no filtrar".
    private static string? LikePattern(string? term)
    {
        var trimmed = term?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : $"%{EscapeLikeWildcards(trimmed)}%";
    }

    // Los cuatro filtros del listado, aplicados una sola vez para los dos caminos que los usan
    // (la pagina del listado y el recorrido de la exportacion). Los bloques van repetidos y no
    // detras de un helper generico sobre un selector de propiedad: EF Core no traduce
    // `EF.Functions.ILike` detras de una expresion invocada de forma generica, y forzarlo
    // terminaria evaluando el filtro en memoria en vez de en la base.
    private IQueryable<Customer> FilteredQuery(
        Guid tenantId,
        string? searchPattern,
        string? namePattern,
        string? identificationPattern,
        string? cucPattern)
    {
        var query = dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId);

        // El criterio combinado original: un solo termino, OR entre los tres campos. Sigue
        // vivo para el combobox de clientes de quotes, que necesita un unico cuadro de texto.
        if (searchPattern is not null)
        {
            query = query.Where(customer =>
                EF.Functions.ILike(customer.Name, searchPattern, LikeEscapeCharacter) ||
                EF.Functions.ILike(
                    customer.IdentificationNumber, searchPattern, LikeEscapeCharacter) ||
                EF.Functions.ILike(customer.Cuc, searchPattern, LikeEscapeCharacter));
        }

        // Tres cajas separadas en el listado (CLI-FILTROS-01), cada una filtra su propia columna
        // y se combinan con AND cuando el llamador manda mas de una.
        if (namePattern is not null)
        {
            query = query.Where(customer =>
                EF.Functions.ILike(customer.Name, namePattern, LikeEscapeCharacter));
        }

        if (identificationPattern is not null)
        {
            query = query.Where(customer =>
                EF.Functions.ILike(
                    customer.IdentificationNumber, identificationPattern, LikeEscapeCharacter));
        }

        if (cucPattern is not null)
        {
            query = query.Where(customer =>
                EF.Functions.ILike(customer.Cuc, cucPattern, LikeEscapeCharacter));
        }

        return query;
    }

    // Orden por CUC y no por relevancia como SearchAsync: el CUC es unico dentro del tenant, asi
    // que desempata siempre. Recorrer en lotes un orden que empata puede saltear o repetir filas
    // entre una consulta y la siguiente, y eso en un archivo exportado no lo ve nadie.
    public async Task<IReadOnlyList<Customer>> ListForExportAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? identificationNumber,
        string? cuc,
        int skip,
        int take,
        CancellationToken cancellationToken) =>
        await FilteredQuery(
                tenantId,
                LikePattern(search),
                LikePattern(name),
                LikePattern(identificationNumber),
                LikePattern(cuc))
            .OrderBy(customer => customer.Cuc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<(IReadOnlyList<Customer> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? search,
        string? name,
        string? identificationNumber,
        string? cuc,
        IReadOnlyCollection<Guid>? cityIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var searchPattern = LikePattern(search);
        var namePattern = LikePattern(name);
        var identificationPattern = LikePattern(identificationNumber);
        var cucPattern = LikePattern(cuc);
        var query = FilteredQuery(
            tenantId, searchPattern, namePattern, identificationPattern, cucPattern);

        // `null` es "sin filtro"; una coleccion (incluso vacia) filtra por esos ids exactos —
        // Departamento ya se tradujo a ids de ciudad en ListCustomersHandler, asi que aca no hay
        // nada que resolver, solo aplicar el IN. Va aca y no en FilteredQuery porque la
        // exportacion no filtra por ciudad.
        if (cityIds is not null)
        {
            query = query.Where(customer => cityIds.Contains(customer.CityId));
        }

        // El total se cuenta sobre la consulta **ya filtrada** y antes de paginar: es cuantos
        // clientes coinciden con la busqueda, no cuantos tiene el tenant. Contar despues del Skip
        // devolveria como mucho pageSize y la UI dibujaria una sola pagina siempre.
        var total = await query.CountAsync(cancellationToken);

        // Con algun filtro de texto activo, el resultado se ordena por relevancia — la fila que
        // mas se parece al termino buscado va primero, no la que gana alfabeticamente. `search`
        // (el combobox de quotes) usa un solo termino contra los tres campos; name/
        // identificationNumber/cuc (el listado) usan un termino independiente por campo.
        // `TrigramsSimilarity(columna, "")` da 0, asi que sumar los tres terminos funciona igual
        // aunque el llamador solo haya llenado uno.
        // Los `?.`/`??` tienen que resolverse ANTES del lambda: un operador null-propagating
        // dentro de un árbol de expresión (lo que EF Core traduce a SQL) no compila — CS8072 —
        // aunque el operando sea una variable capturada y no el parámetro del lambda.
        var searchTerm = search?.Trim() ?? string.Empty;
        var nameTerm = name?.Trim() ?? string.Empty;
        var identificationTerm = identificationNumber?.Trim() ?? string.Empty;
        var cucTerm = cuc?.Trim() ?? string.Empty;

        // Sin filtro de texto, el mas nuevo va primero: es el orden que importa para operar (el
        // cliente que alguien acaba de cargar aparece arriba de entrada, sin tener que buscarlo),
        // no el alfabetico que solo ayuda a ubicar un nombre que ya se conoce.
        var orderedQuery = searchPattern is not null
            ? query.OrderByDescending(customer =>
                EF.Functions.TrigramsSimilarity(customer.Name, searchTerm) +
                EF.Functions.TrigramsSimilarity(customer.IdentificationNumber, searchTerm) +
                EF.Functions.TrigramsSimilarity(customer.Cuc, searchTerm))
            : namePattern is not null || identificationPattern is not null || cucPattern is not null
                ? query.OrderByDescending(customer =>
                    EF.Functions.TrigramsSimilarity(customer.Name, nameTerm) +
                    EF.Functions.TrigramsSimilarity(customer.IdentificationNumber, identificationTerm) +
                    EF.Functions.TrigramsSimilarity(customer.Cuc, cucTerm))
                : query.OrderByDescending(customer => customer.CreatedAt);

        var items = await orderedQuery
            .ThenBy(customer => customer.Name)
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

    public async Task<IReadOnlyDictionary<(IdentificationType Type, string Number), CustomerId>>
        FindExistingIdentificationsAsync(
            Guid tenantId,
            IReadOnlyCollection<(IdentificationType Type, string Number)> identifications,
            CancellationToken cancellationToken)
    {
        if (identifications.Count == 0)
        {
            return new Dictionary<(IdentificationType, string), CustomerId>();
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
            .Select(customer => new
            {
                customer.Id,
                customer.IdentificationType,
                customer.IdentificationNumber
            })
            .ToListAsync(cancellationToken);

        var ownerByIdentification = candidates.ToDictionary(
            candidate => (candidate.IdentificationType, candidate.IdentificationNumber),
            candidate => candidate.Id);

        return identifications
            .Where(ownerByIdentification.ContainsKey)
            .ToDictionary(identification => identification, identification => ownerByIdentification[identification]);
    }

    public async Task<IReadOnlySet<Guid>> SearchIdsByIdentificationNumberAsync(
        Guid tenantId,
        string term,
        CancellationToken cancellationToken)
    {
        var pattern = LikePattern(term);
        if (pattern is null)
        {
            return new HashSet<Guid>();
        }

        var ids = await dbContext.Customers
            .AsNoTracking()
            .Where(customer =>
                customer.TenantId == tenantId &&
                EF.Functions.ILike(customer.IdentificationNumber, pattern, LikeEscapeCharacter))
            .Select(customer => customer.Id.Value)
            .ToListAsync(cancellationToken);

        return ids.ToHashSet();
    }

    public async Task<IReadOnlyDictionary<string, CustomerId>> FindIdsByCucSuffixAsync(
        Guid tenantId,
        IReadOnlyCollection<string> suffixes,
        CancellationToken cancellationToken)
    {
        if (suffixes.Count == 0)
        {
            return new Dictionary<string, CustomerId>();
        }

        // Igual criterio que FindExistingIdentificationsAsync: un WHERE amplio en la base (solo
        // tenant, que ya usa indice) y el match exacto del sufijo en memoria — EF/Npgsql no
        // traduce bien un `Contains` sobre un substring calculado por fila, y el conjunto ya
        // filtrado por tenant es chico.
        var tenantCustomers = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId)
            .Select(customer => new { customer.Id, customer.Cuc })
            .ToListAsync(cancellationToken);

        var suffixSet = suffixes.ToHashSet();
        var result = new Dictionary<string, CustomerId>();
        foreach (var customer in tenantCustomers)
        {
            var suffix = Customer.StableSuffixOf(customer.Cuc);
            if (suffixSet.Contains(suffix))
            {
                result[suffix] = customer.Id;
            }
        }

        return result;
    }

    public void Add(Customer customer) => dbContext.Customers.Add(customer);
}
