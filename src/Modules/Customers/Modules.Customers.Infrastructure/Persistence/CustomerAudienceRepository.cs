using Microsoft.EntityFrameworkCore;
using Modules.Customers.Application;
using Modules.Customers.Domain;

namespace Modules.Customers.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-11 §4.4: la audiencia de una campaña y el contacto de un cliente, para Messaging. La audiencia es la
/// unión de «tiene alguna de estas etiquetas» y «es uno de estos clientes», evaluada con <c>EXISTS</c> y no con un
/// join: así un cliente que cumple por las dos vías, o que tiene varias de las etiquetas, aparece una sola vez.
/// </summary>
internal sealed class CustomerAudienceRepository(CustomersDbContext dbContext) : ICustomerAudienceRepository
{
    public async Task<CustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        var id = new CustomerId(customerId);
        var row = await dbContext.Customers
            .AsNoTracking()
            .Where(customer => customer.TenantId == tenantId && customer.Id == id)
            .Select(customer => new
            {
                customer.Name,
                customer.PhoneE164,
                customer.WhatsAppUserId,
                customer.IsActive,
                customer.MarketingOptOut,
            })
            .SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new CustomerContact(customerId, row.Name, row.PhoneE164, row.WhatsAppUserId, row.IsActive, row.MarketingOptOut);
    }

    public async Task<IReadOnlyList<CustomerContact>> ListAudienceBatchAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> tagIds,
        IReadOnlyCollection<Guid> customerIds,
        Guid? afterId,
        int take,
        CancellationToken cancellationToken)
    {
        // Paginado por clave (`id > @after`) y no por OFFSET: recorrer 100 000 clientes con OFFSET relee los
        // anteriores en cada lote. La comparación va en SQL porque el orden de bytes de un uuid en PostgreSQL no es el
        // de Guid.CompareTo en .NET, y mezclar los dos saltaría o repetiría filas.
        var after = afterId ?? Guid.Empty;
        var rows = await InAudience(
                dbContext.Customers.FromSql(
                    $"SELECT * FROM customers.customers WHERE tenant_id = {tenantId} AND id > {after}"),
                tenantId,
                tagIds,
                customerIds)
            .AsNoTracking()
            .OrderBy(customer => customer.Id)
            .Take(take)
            .Select(customer => new
            {
                customer.Id,
                customer.Name,
                customer.PhoneE164,
                customer.WhatsAppUserId,
                customer.IsActive,
                customer.MarketingOptOut,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new CustomerContact(row.Id.Value, row.Name, row.PhoneE164, row.WhatsAppUserId, row.IsActive, row.MarketingOptOut))
            .ToArray();
    }

    public async Task<AudienceCount> CountAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        var groups = await InAudience(dbContext.Customers, tenantId, tagIds, customerIds)
            .AsNoTracking()
            .GroupBy(customer => new
            {
                customer.IsActive,
                customer.MarketingOptOut,
                HasWhatsApp = customer.PhoneE164 != null || customer.WhatsAppUserId != null,
            })
            .Select(group => new { group.Key.IsActive, group.Key.MarketingOptOut, group.Key.HasWhatsApp, Count = group.Count() })
            .ToListAsync(cancellationToken);

        // Cubos excluyentes con precedencia: inactivo, luego sin WhatsApp, luego con opt-out.
        var inactive = groups.Where(group => !group.IsActive).Sum(group => group.Count);
        var withoutWhatsApp = groups.Where(group => group.IsActive && !group.HasWhatsApp).Sum(group => group.Count);
        var optedOut = groups.Where(group => group.IsActive && group.HasWhatsApp && group.MarketingOptOut).Sum(group => group.Count);

        return new AudienceCount(groups.Sum(group => group.Count), inactive, optedOut, withoutWhatsApp);
    }

    // UPDATE condicional, como los del BSUID: dos respuestas 131050 seguidas no se pisan y la primera deja el rastro.
    // No sube la versión del cliente.
    public async Task MarkMarketingOptOutAsync(
        Guid tenantId, Guid customerId, MarketingConsentSource source, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var id = new CustomerId(customerId);
        _ = await dbContext.Customers
            .Where(customer => customer.TenantId == tenantId && customer.Id == id && !customer.MarketingOptOut)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(customer => customer.MarketingOptOut, true)
                    .SetProperty(customer => customer.MarketingOptOutAt, (DateTimeOffset?)occurredAt)
                    .SetProperty(customer => customer.MarketingOptOutSource, (MarketingConsentSource?)source),
                cancellationToken);
    }

    private IQueryable<Customer> InAudience(
        IQueryable<Customer> customers, Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds)
    {
        var tags = tagIds.Select(id => new TagId(id)).ToArray();
        var ids = customerIds.Select(id => new CustomerId(id)).ToArray();

        return customers.Where(customer =>
            customer.TenantId == tenantId
            && (ids.Contains(customer.Id)
                || dbContext.CustomerTagAssignments.Any(assignment =>
                    assignment.TenantId == tenantId && assignment.CustomerId == customer.Id && tags.Contains(assignment.TagId))));
    }
}
