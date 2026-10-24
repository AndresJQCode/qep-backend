using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

internal static class CustomerIdentificationSql
{
    /// <summary>
    /// La única copia de la expresión del índice en código; la migración <c>AddCustomerIdentificationNormalizedIndex</c> la
    /// repite literal (las migraciones no leen constantes) y <c>TheMatchQueryUsesTheNormalizedIndex</c> amarra que coincidan.
    /// </summary>
    public const string NormalizedNumber = "upper(regexp_replace(identification_number, '[^0-9A-Za-z]', '', 'g'))";
}

internal sealed class CustomerDuplicateQueries(CustomersDbContext dbContext) : ICustomerDuplicateQueries
{
    public async Task<IReadOnlyList<Customer>> FindByNormalizedIdentificationAsync(
        Guid tenantId, string normalizedNumber, Guid? excludeCustomerId, int take, CancellationToken cancellationToken)
    {
        // SELECT * para que EF reciba todas las columnas mapeadas (un FromSql crudo no las completa), y LINQ encima.
        var query = dbContext.Customers
            .FromSqlRaw(
                $"SELECT * FROM customers.customers WHERE tenant_id = {{0}} AND identification_type IS NOT NULL " +
                $"AND identification_number IS NOT NULL AND {CustomerIdentificationSql.NormalizedNumber} = {{1}}",
                tenantId,
                normalizedNumber)
            .AsNoTracking();

        if (excludeCustomerId is { } excluded)
        {
            var excludedId = new CustomerId(excluded);
            query = query.Where(customer => customer.Id != excludedId);
        }

        return await query
            .OrderBy(customer => customer.CreatedAt).ThenBy(customer => customer.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

#pragma warning disable EF1003 // Sólo se interpolan KeyOf y FilterOf: expresiones fijas de este archivo; todo valor del cliente es parámetro.
    // La clave del grupo y su filtro por clase. La clave es una expresión fija de este archivo (nunca texto del cliente), así que
    // se interpola; los valores del cliente van siempre como parámetros. El número normalizado vacío ("---") no es un grupo.
    private static string KeyOf(CustomerAnomalyKind kind) =>
        kind == CustomerAnomalyKind.SharedPhone ? "phone_e164" : CustomerIdentificationSql.NormalizedNumber;

    private static string FilterOf(CustomerAnomalyKind kind) =>
        kind == CustomerAnomalyKind.SharedPhone
            ? "phone_e164 IS NOT NULL"
            : $"identification_number IS NOT NULL AND {CustomerIdentificationSql.NormalizedNumber} <> ''";

    public async Task<IReadOnlyDictionary<CustomerAnomalyKind, int>> CountAnomalyGroupsAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<CustomerAnomalyKind, int>();
        foreach (var kind in Enum.GetValues<CustomerAnomalyKind>())
        {
            counts[kind] = await dbContext.Database
                .SqlQueryRaw<int>(
                    $"SELECT count(*)::int AS \"Value\" FROM (SELECT 1 FROM customers.customers WHERE tenant_id = {{0}} " +
                    $"AND {FilterOf(kind)} GROUP BY {KeyOf(kind)} HAVING count(*) > 1) AS groups",
                    tenantId)
                .SingleAsync(cancellationToken);
        }

        return counts;
    }

    public async Task<IReadOnlyList<CustomerAnomalyGroupRow>> ListAnomalyGroupsAsync(
        Guid tenantId, CustomerAnomalyKind kind, int skip, int take, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database
            .SqlQueryRaw<GroupRow>(
                $"SELECT {KeyOf(kind)} AS \"Key\", count(*)::int AS \"Size\" FROM customers.customers " +
                $"WHERE tenant_id = {{0}} AND {FilterOf(kind)} GROUP BY {KeyOf(kind)} HAVING count(*) > 1 " +
                "ORDER BY count(*) DESC, 1 LIMIT {1} OFFSET {2}",
                tenantId,
                take,
                skip)
            .ToListAsync(cancellationToken);

        return rows.Select(row => new CustomerAnomalyGroupRow(row.Key, row.Size)).ToArray();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Customer>>> ListAnomalyGroupMembersAsync(
        Guid tenantId, CustomerAnomalyKind kind, IReadOnlyCollection<string> keys, int perGroup, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<Customer>>();
        }

        // Una ventana por clave: trae a lo sumo perGroup + 1 filas de cada grupo y no el grupo entero. El id y la clave van
        // primero, y los clientes se leen después por id (un FromSql con ventana no se puede componer sin perder columnas).
        var members = await dbContext.Database
            .SqlQueryRaw<MemberRow>(
                $"SELECT id AS \"Id\", key AS \"Key\" FROM (SELECT id, {KeyOf(kind)} AS key, " +
                $"row_number() OVER (PARTITION BY {KeyOf(kind)} ORDER BY created_at, id) AS position " +
                $"FROM customers.customers WHERE tenant_id = {{0}} AND {FilterOf(kind)} AND {KeyOf(kind)} = ANY({{1}})) AS ranked " +
                "WHERE position <= {2} ORDER BY key, position",
                tenantId,
                keys.ToArray(),
                perGroup + 1)
            .ToListAsync(cancellationToken);

        var ids = members.Select(member => new CustomerId(member.Id)).ToArray();
        var customers = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && ids.Contains(customer.Id))
            .ToDictionaryAsync(customer => customer.Id, cancellationToken);

        return members
            .GroupBy(member => member.Key)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Customer>)group.Select(member => customers[new CustomerId(member.Id)]).ToArray());
    }

#pragma warning restore EF1003

    private sealed class GroupRow
    {
        public string Key { get; set; } = string.Empty;

        public int Size { get; set; }
    }

    private sealed class MemberRow
    {
        public Guid Id { get; set; }

        public string Key { get; set; } = string.Empty;
    }
}
