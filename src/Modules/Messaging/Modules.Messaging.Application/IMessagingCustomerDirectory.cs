namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-10 §8.2: quien escribió, como lo ve Messaging (el <c>waId</c> son los dígitos sin «+»).</summary>
public sealed record MessagingContact(string UserId, string? WaId, string? ProfileName, string? Username);

/// <summary><c>Created</c> decide el evento: <c>CustomerCreated</c> si se creó, si no <c>CustomerLinked</c> (§8.2).</summary>
public sealed record MessagingEnsuredCustomer(Guid CustomerId, bool Created);

/// <summary>Spec 2026-10-09 §6.5: los clientes de QEP por teléfono, con adaptador en Bootstrapper sobre
/// <c>ICustomerPhoneDirectory</c> (Messaging no referencia Customers). El <c>waId</c> de Meta son los
/// dígitos del E.164; el adaptador traduce. «Nombres y clientes» de §7.6: una consulta por página.</summary>
public interface IMessagingCustomerDirectory
{
    /// <summary>Un cliente por <c>waId</c> (D-M7); los que no tienen cliente no aparecen.</summary>
    Task<IReadOnlyDictionary<string, CustomerRefDto>> MatchAsync(Guid tenantId, IReadOnlyCollection<string> waIds, CancellationToken cancellationToken);

    /// <summary>Los <c>waId</c> de los clientes cuyo nombre contiene <paramref name="term"/>, para el
    /// <c>wa_id = ANY(@phones)</c> de la búsqueda de la lista (§8.7). Con tope.</summary>
    Task<IReadOnlyList<string>> FindWaIdsByNameAsync(Guid tenantId, string term, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-10 §8.2: el cliente de QEP de esta persona, creándolo incompleto si no existe. Corre antes
    /// y fuera de la transacción de la ingesta; es idempotente por BSUID.</summary>
    Task<MessagingEnsuredCustomer> EnsureAsync(Guid tenantId, MessagingContact contact, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-10 §8.3: el cliente con el BSUID anterior pasa al nuevo. Si el nuevo ya es de otro cliente no
    /// se toca (lo registra el adaptador). El teléfono del cliente no cambia: es dato maestro.</summary>
    Task ReplaceUserIdAsync(Guid tenantId, string previous, string current, CancellationToken cancellationToken);
}
