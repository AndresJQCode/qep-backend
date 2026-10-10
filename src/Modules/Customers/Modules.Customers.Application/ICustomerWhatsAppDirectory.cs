namespace Modules.Customers.Application;

/// <summary>Quien escribió por WhatsApp, como lo ve Customers: BSUID, teléfono en E.164 con «+» si Meta lo
/// mandó, nombre de perfil y usuario si los tiene.</summary>
public sealed record WhatsAppContact(string UserId, string? PhoneE164, string? ProfileName, string? Username);

public enum EnsureOutcome
{
    /// <summary>Ya había un cliente con ese BSUID.</summary>
    Existing,

    /// <summary>Se encontró por teléfono (D-M7) y se le puso el BSUID si no tenía otro (D-A6).</summary>
    Linked,

    /// <summary>Se creó un cliente incompleto.</summary>
    Created,
}

public sealed record EnsuredCustomer(Guid CustomerId, EnsureOutcome Outcome);

public sealed record CustomerWhatsAppRef(Guid Id, string Name, bool IsComplete);

/// <summary>
/// Spec 2026-10-10 §6.2: lo que Messaging necesita de Customers para atar una conversación a un cliente, con
/// adaptador en Bootstrapper (<c>MessagingCustomerDirectory</c>). Messaging no referencia Customers.
/// </summary>
public interface ICustomerWhatsAppDirectory
{
    /// <summary>§8.2: por BSUID → por teléfono → incompleto nuevo. Idempotente: un reintento encuentra el
    /// cliente por BSUID. Dos llamadas concurrentes con el mismo BSUID nuevo devuelven el mismo cliente.</summary>
    Task<EnsuredCustomer> EnsureAsync(Guid tenantId, WhatsAppContact contact, CancellationToken cancellationToken);

    /// <summary>§8.3: el cliente con <paramref name="previous"/> pasa a <paramref name="current"/>. <c>false</c>
    /// si nadie tenía el anterior o si el nuevo ya es de otro cliente (no se toca). El teléfono no cambia.</summary>
    Task<bool> ReplaceWhatsAppUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken);

    /// <summary>§6.1.2: nombre y completitud de los clientes de una página; los que no están no aparecen.</summary>
    Task<IReadOnlyDictionary<Guid, CustomerWhatsAppRef>> FindRefsAsync(Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>§6.1.6: ids de los clientes cuyo nombre contiene <paramref name="term"/> (usa
    /// <c>IX_customers_name_trgm</c>), hasta <paramref name="cap"/>.</summary>
    Task<IReadOnlyList<Guid>> FindIdsByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken);
}
