namespace Modules.Messaging.Application;

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
}
