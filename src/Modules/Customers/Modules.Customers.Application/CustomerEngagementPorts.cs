using Modules.Customers.Domain;

namespace Modules.Customers.Application;

// Spec 2026-10-11 §4: puertos de las notas, las etiquetas y la audiencia. Todo método recibe tenantId primero, como el
// resto del módulo: el filtro de tenant es parte de la consulta.

public interface ICustomerNoteRepository
{
    /// <summary>De la más nueva a la más vieja (desempata por id, que es UUIDv7 y por lo tanto ordenado).</summary>
    Task<IReadOnlyList<CustomerNote>> ListByCustomerAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken);

    /// <summary>Con tracking. Acotada al cliente: la nota de otro cliente del mismo tenant no se alcanza por esta ruta.</summary>
    Task<CustomerNote?> FindAsync(Guid tenantId, CustomerId customerId, CustomerNoteId noteId, CancellationToken cancellationToken);

    void Add(CustomerNote note);

    void Remove(CustomerNote note);
}

/// <summary>Una etiqueta con cuántos clientes la tienen, para <c>TagDto.customerCount</c>.</summary>
public sealed record TagWithCount(Tag Tag, int CustomerCount);

public interface ICustomerTagRepository
{
    /// <summary>Todas las del tenant, por nombre sin distinguir mayúsculas.</summary>
    Task<IReadOnlyList<TagWithCount>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>Las de un cliente, con el mismo orden y el mismo conteo que <see cref="ListAsync"/>.</summary>
    Task<IReadOnlyList<TagWithCount>> ListForCustomerAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken);

    /// <summary>Con tracking.</summary>
    Task<Tag?> FindAsync(Guid tenantId, TagId tagId, CancellationToken cancellationToken);

    /// <summary>Las que existen en el tenant entre las pedidas; la de otro tenant no aparece, igual que una inexistente.</summary>
    Task<IReadOnlyList<Tag>> ListByIdsAsync(Guid tenantId, IReadOnlyCollection<TagId> tagIds, CancellationToken cancellationToken);

    Task<int> CountAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<int> CountCustomersAsync(Guid tenantId, TagId tagId, CancellationToken cancellationToken);

    /// <summary>Spec qep-frontend 2026-10-15 (filtros de la bandeja): los clientes con esa etiqueta, hasta <paramref name="cap"/>. La de
    /// otro tenant no devuelve nada, igual que una inexistente.</summary>
    Task<IReadOnlyList<CustomerId>> ListCustomerIdsAsync(Guid tenantId, TagId tagId, int cap, CancellationToken cancellationToken);

    /// <summary>Las etiquetas de varios clientes en una consulta (los chips de una página de la bandeja). Sólo los que tienen alguna.</summary>
    Task<IReadOnlyList<(CustomerId Customer, Tag Tag)>> ListForCustomersAsync(
        Guid tenantId, IReadOnlyCollection<CustomerId> customerIds, CancellationToken cancellationToken);

    /// <summary>Con tracking: <c>SetCustomerTagsHandler</c> las quita o conserva.</summary>
    Task<IReadOnlyList<CustomerTagAssignment>> ListAssignmentsAsync(Guid tenantId, CustomerId customerId, CancellationToken cancellationToken);

    void Add(Tag tag);

    /// <summary>Borra la etiqueta; sus asignaciones se van con ella (<c>ON DELETE CASCADE</c>).</summary>
    void Remove(Tag tag);

    void AddAssignment(CustomerTagAssignment assignment);

    void RemoveAssignment(CustomerTagAssignment assignment);
}

/// <summary>
/// El nombre que se muestra del autor de una nota. Adaptador en Bootstrapper sobre Tenancy e Identity: Customers no
/// referencia esos módulos. Quien ya no existe no aparece en el diccionario, y el caso de uso lo muestra como
/// «Usuario eliminado»: <c>displayName</c> nunca viaja <c>null</c>.
/// </summary>
public interface ICustomerAuthorNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

/// <summary>Quién es el cliente para efectos de mensajería (spec 2026-10-11 §4.4). Sin teléfono ni BSUID no se le puede escribir.
/// <c>MarketingOptOut</c> es del número (spec 2026-10-24 §4.4): vale <c>true</c> si la ficha, o cualquier otro cliente del
/// tenant con el mismo teléfono, tiene la bandera; una ficha sin teléfono sólo mira la suya.</summary>
public sealed record CustomerContact(
    Guid CustomerId, string Name, string? PhoneE164, string? WhatsAppUserId, bool IsActive, bool MarketingOptOut);

/// <summary>
/// Cuántos clientes tiene una audiencia y por qué algunos no recibirían nada. Los cubos son excluyentes y con
/// precedencia: <c>Inactive</c>, luego <c>WithoutWhatsApp</c> (ni teléfono ni BSUID), luego <c>OptedOut</c>. Así quien
/// pinta «le llegará a N de M» puede restar sin contar dos veces. <c>OptedOut</c> se cuenta sin mirar la categoría
/// de la plantilla: restarlo o no (sólo <c>MARKETING</c>) lo decide quien conoce la plantilla.
/// </summary>
public sealed record AudienceCount(int Total, int Inactive, int OptedOut, int WithoutWhatsApp);

public interface ICustomerAudienceRepository
{
    Task<CustomerContact?> FindContactAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    /// <summary>Un lote de la audiencia (clientes con alguna de las etiquetas, más los clientes sueltos), sin duplicados,
    /// ordenado por id y con id mayor que <paramref name="afterId"/>.</summary>
    Task<IReadOnlyList<CustomerContact>> ListAudienceBatchAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> tagIds,
        IReadOnlyCollection<Guid> customerIds,
        Guid? afterId,
        int take,
        CancellationToken cancellationToken);

    Task<AudienceCount> CountAudienceAsync(
        Guid tenantId, IReadOnlyCollection<Guid> tagIds, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);

    /// <summary><c>UPDATE</c> condicional (<c>WHERE NOT marketing_opt_out</c>): idempotente y sin pisar el rastro
    /// original. No sube la versión del cliente.</summary>
    Task MarkMarketingOptOutAsync(
        Guid tenantId, Guid customerId, MarketingConsentSource source, DateTimeOffset occurredAt, CancellationToken cancellationToken);
}

/// <summary>Nombre y color (por su nombre de contrato: <c>blue</c>) de una etiqueta, para quien la nombra sin ser Customers.</summary>
public sealed record CustomerTagRef(Guid Id, string Name, string Color);

/// <summary>
/// Spec 2026-10-11 §6.2 (E3): las etiquetas del tenant entre las pedidas, para el detalle de una campaña. Adaptador en Bootstrapper hacia
/// Messaging, que no referencia Customers. La de otro tenant y la borrada no aparecen: el detalle las omite.
/// </summary>
public interface ICustomerTagLookup
{
    Task<IReadOnlyList<CustomerTagRef>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken);

    /// <summary>Spec qep-frontend 2026-10-15: los clientes con la etiqueta, para el filtro de la bandeja (con tope).</summary>
    Task<IReadOnlyList<Guid>> FindCustomerIdsAsync(Guid tenantId, Guid tagId, int cap, CancellationToken cancellationToken);

    /// <summary>Las etiquetas de cada cliente, por nombre; el que no tiene ninguna no aparece.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<CustomerTagRef>>> FindForCustomersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken);
}

public sealed class CustomerTagLookup(ICustomerTagRepository tags) : ICustomerTagLookup
{
    public async Task<IReadOnlyList<CustomerTagRef>> FindAsync(Guid tenantId, IReadOnlyCollection<Guid> tagIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        if (tagIds.Count == 0)
        {
            return [];
        }

        var found = await tags.ListByIdsAsync(tenantId, [.. tagIds.Select(id => new TagId(id))], cancellationToken);
        return [.. found.Select(tag => new CustomerTagRef(tag.Id.Value, tag.Name, tag.Color.ToWireValue()))];
    }

    public async Task<IReadOnlyList<Guid>> FindCustomerIdsAsync(Guid tenantId, Guid tagId, int cap, CancellationToken cancellationToken) =>
        [.. (await tags.ListCustomerIdsAsync(tenantId, new TagId(tagId), cap, cancellationToken)).Select(id => id.Value)];

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CustomerTagRef>>> FindForCustomersAsync(
        Guid tenantId, IReadOnlyCollection<Guid> customerIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customerIds);
        if (customerIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<CustomerTagRef>>();
        }

        var rows = await tags.ListForCustomersAsync(tenantId, [.. customerIds.Select(id => new CustomerId(id))], cancellationToken);
        return rows
            .GroupBy(row => row.Customer.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CustomerTagRef>)[.. group
                    .OrderBy(row => row.Tag.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Tag.Id.Value)
                    .Select(row => new CustomerTagRef(row.Tag.Id.Value, row.Tag.Name, row.Tag.Color.ToWireValue()))]);
    }
}
