using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

internal sealed class CustomerTagRepository(CustomersDbContext dbContext) : ICustomerTagRepository
{
    // Hasta 100 etiquetas por tenant (Tag.MaxPerTenant): el orden por nombre sin distinguir mayúsculas se hace en
    // memoria, sin ToLower() en SQL (CA1304/CA1311) y sin depender de la collation de la base.
    public async Task<IReadOnlyList<TagWithCount>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Tags
            .AsNoTracking()
            .Where(tag => tag.TenantId == tenantId)
            .Select(tag => new
            {
                Tag = tag,
                Count = dbContext.CustomerTagAssignments.Count(assignment => assignment.TagId == tag.Id),
            })
            .ToListAsync(cancellationToken);

        return OrderedByName(rows.Select(row => new TagWithCount(row.Tag, row.Count)));
    }

    public async Task<IReadOnlyList<TagWithCount>> ListForCustomerAsync(
        Guid tenantId, CustomerId customerId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Tags
            .AsNoTracking()
            .Where(tag => tag.TenantId == tenantId
                && dbContext.CustomerTagAssignments.Any(assignment => assignment.CustomerId == customerId && assignment.TagId == tag.Id))
            .Select(tag => new
            {
                Tag = tag,
                Count = dbContext.CustomerTagAssignments.Count(assignment => assignment.TagId == tag.Id),
            })
            .ToListAsync(cancellationToken);

        return OrderedByName(rows.Select(row => new TagWithCount(row.Tag, row.Count)));
    }

    // Con tracking: UpdateTagHandler y DeleteTagHandler mutan el agregado.
    public Task<Tag?> FindAsync(Guid tenantId, TagId tagId, CancellationToken cancellationToken) =>
        dbContext.Tags.SingleOrDefaultAsync(tag => tag.TenantId == tenantId && tag.Id == tagId, cancellationToken);

    public async Task<IReadOnlyList<Tag>> ListByIdsAsync(
        Guid tenantId, IReadOnlyCollection<TagId> tagIds, CancellationToken cancellationToken) =>
        tagIds.Count == 0
            ? []
            : await dbContext.Tags
                .AsNoTracking()
                .Where(tag => tag.TenantId == tenantId && tagIds.Contains(tag.Id))
                .ToListAsync(cancellationToken);

    public Task<int> CountAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Tags.CountAsync(tag => tag.TenantId == tenantId, cancellationToken);

    public Task<int> CountCustomersAsync(Guid tenantId, TagId tagId, CancellationToken cancellationToken) =>
        dbContext.CustomerTagAssignments.CountAsync(
            assignment => assignment.TenantId == tenantId && assignment.TagId == tagId, cancellationToken);

    public async Task<IReadOnlyList<CustomerTagAssignment>> ListAssignmentsAsync(
        Guid tenantId, CustomerId customerId, CancellationToken cancellationToken) =>
        await dbContext.CustomerTagAssignments
            .Where(assignment => assignment.TenantId == tenantId && assignment.CustomerId == customerId)
            .ToListAsync(cancellationToken);

    public void Add(Tag tag) => dbContext.Tags.Add(tag);

    // El CASCADE de la base borra las asignaciones; no hace falta cargarlas.
    public void Remove(Tag tag) => dbContext.Tags.Remove(tag);

    public void AddAssignment(CustomerTagAssignment assignment) => dbContext.CustomerTagAssignments.Add(assignment);

    public void RemoveAssignment(CustomerTagAssignment assignment) => dbContext.CustomerTagAssignments.Remove(assignment);

    private static List<TagWithCount> OrderedByName(IEnumerable<TagWithCount> items) =>
        [.. items.OrderBy(item => item.Tag.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Tag.Id.Value)];
}
