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
}
