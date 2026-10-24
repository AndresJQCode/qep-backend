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
        var row = await ProjectContact(
                dbContext.Customers.AsNoTracking().Where(customer => customer.TenantId == tenantId && customer.Id == id),
                tenantId)
            .SingleOrDefaultAsync(cancellationToken);

        return row is null ? null : ToContact(row);
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
        var rows = await ProjectContact(
                InAudience(
                        dbContext.Customers.FromSql(
                            $"SELECT * FROM customers.customers WHERE tenant_id = {tenantId} AND id > {after}"),
                        tenantId,
                        tagIds,
                        customerIds)
                    .AsNoTracking()
                    .OrderBy(customer => customer.Id)
                    .Take(take),
                tenantId)
            .ToListAsync(cancellationToken);

        return rows.Select(ToContact).ToArray();
    }

    public async Task<AudienceCount> CountAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        // Cuatro conteos y no un GroupBy: EF no traduce el GroupBy sobre una proyección con la subconsulta correlacionada
        // del opt-out por número. Cada conteo es un solo SELECT count(*) sobre la misma proyección de ProjectContact.
        var rows = ProjectContact(InAudience(dbContext.Customers, tenantId, tagIds, customerIds).AsNoTracking(), tenantId);
        var total = await rows.CountAsync(cancellationToken);
        // Cubos excluyentes con precedencia: inactivo, luego sin WhatsApp, luego con opt-out.
        var inactive = await rows.CountAsync(row => !row.IsActive, cancellationToken);
        var withoutWhatsApp = await rows.CountAsync(
            row => row.IsActive && row.PhoneE164 == null && row.WhatsAppUserId == null, cancellationToken);
        var optedOut = await rows.CountAsync(
            row => row.IsActive && (row.PhoneE164 != null || row.WhatsAppUserId != null) && row.OptedOut, cancellationToken);

        return new AudienceCount(total, inactive, optedOut, withoutWhatsApp);
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

    // Clase con inicializadores y no record posicional: EF compone un Where/Count sobre un member-init, no sobre un constructor.
    private sealed class ContactRow
    {
        public required CustomerId Id { get; init; }
        public required string Name { get; init; }
        public required string? PhoneE164 { get; init; }
        public required string? WhatsAppUserId { get; init; }
        public required bool IsActive { get; init; }
        public required bool OptedOut { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
    }

    private static CustomerContact ToContact(ContactRow row) =>
        new(row.Id.Value, row.Name, row.PhoneE164, row.WhatsAppUserId, row.IsActive, row.OptedOut, row.CreatedAt);

    // Spec 2026-10-24 §4.4: el opt-out de marketing es del número. Un cliente cuenta como excluido si él, o cualquier
    // otro cliente del tenant con el mismo phone_e164, tiene la bandera; sin teléfono sólo mira la suya. Se calcula al
    // leer y marcar no cambia (MarkMarketingOptOutAsync sigue marcando sólo la ficha). Es el único lugar de la regla:
    // los tres lectores pasan por acá, y lo que se agregue a la proyección se agrega acá. La subconsulta filtra por
    // tenant y usa IX_customers_tenant_phone_e164.
    private IQueryable<ContactRow> ProjectContact(IQueryable<Customer> customers, Guid tenantId) =>
        customers.Select(customer => new ContactRow
        {
            Id = customer.Id,
            Name = customer.Name,
            PhoneE164 = customer.PhoneE164,
            WhatsAppUserId = customer.WhatsAppUserId,
            IsActive = customer.IsActive,
            // Spec 2026-10-24 §4.4: el desempate «el más viejo» de una campaña con dos clientes del mismo WhatsApp (D-M7).
            CreatedAt = customer.CreatedAt,
            OptedOut = customer.MarketingOptOut
                || (customer.PhoneE164 != null
                    && dbContext.Customers.Any(other =>
                        other.TenantId == tenantId && other.PhoneE164 == customer.PhoneE164 && other.MarketingOptOut)),
        });

    private IQueryable<Customer> InAudience(
        IQueryable<Customer> customers, Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds)
    {
        var tags = tagIds.Select(id => new TagId(id)).ToArray();
        var ids = customerIds.Select(id => new CustomerId(id)).ToArray();

        return customers.Where(customer =>
            customer.TenantId == tenantId
            // Un fusionado no recibe campañas (spec 2026-10-24 §3.3). `FindContactAsync` no filtra: el destinatario ya congelado se omite como inactivo.
            && customer.MergedIntoId == null
            && (ids.Contains(customer.Id)
                || dbContext.CustomerTagAssignments.Any(assignment =>
                    assignment.TenantId == tenantId && assignment.CustomerId == customer.Id && tags.Contains(assignment.TagId))));
    }
}
