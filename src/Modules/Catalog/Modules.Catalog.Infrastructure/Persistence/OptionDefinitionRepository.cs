using Microsoft.EntityFrameworkCore;
using Modules.Catalog.Application;
using Modules.Catalog.Domain;

namespace Modules.Catalog.Infrastructure.Persistence;

internal sealed class OptionDefinitionRepository(CatalogDbContext dbContext) : IOptionDefinitionRepository
{
    // Por nombre sin mayúsculas: una biblioteca se recorre alfabéticamente, y el índice único ya
    // trata «talla» y «Talla» como la misma. El orden se hace en memoria: las opciones de un
    // tenant son pocas, y así no depende de la intercalación de la base.
    public async Task<IReadOnlyList<OptionDefinition>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var options = await dbContext.OptionDefinitions
            .AsNoTracking()
            .Include(option => option.Values.OrderBy(value => value.Position))
            .Where(option => option.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        return options.OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // Con tracking: los llamadores mutan el agregado y lo guarda la unidad de trabajo.
    public Task<OptionDefinition?> FindAsync(
        Guid tenantId,
        OptionDefinitionId optionId,
        CancellationToken cancellationToken) =>
        dbContext.OptionDefinitions
            .Include(option => option.Values.OrderBy(value => value.Position))
            .SingleOrDefaultAsync(
                option => option.TenantId == tenantId && option.Id == optionId,
                cancellationToken);

    // SQL y no LINQ: product_options y product_variant_option_values son tipos owned sin DbSet.
    // El filtro de tenant va por products y product_variants, que son los que tienen tenant_id.
    public async Task<OptionUsage> GetUsageAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var optionIds = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT po.option_definition_id AS "Value"
            FROM catalog.product_options po
            JOIN catalog.products p ON p.id = po.product_id
            WHERE p.tenant_id = {tenantId}
            """).ToListAsync(cancellationToken);
        var valueIds = await dbContext.Database.SqlQuery<Guid>($"""
            SELECT DISTINCT vov.option_value_id AS "Value"
            FROM catalog.product_variant_option_values vov
            JOIN catalog.product_variants v ON v.id = vov.variant_id
            WHERE v.tenant_id = {tenantId}
            """).ToListAsync(cancellationToken);

        return new OptionUsage(optionIds.ToHashSet(), valueIds.ToHashSet());
    }

    public void Add(OptionDefinition definition) => dbContext.OptionDefinitions.Add(definition);

    public void Remove(OptionDefinition definition) => dbContext.OptionDefinitions.Remove(definition);
}
