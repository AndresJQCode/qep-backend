namespace Modules.Customers.Application;

public sealed record CustomerPhoneMatch(Guid Id, string Name, bool IsComplete);

/// <summary>
/// Spec 2026-10-09 §6.5: lo que Messaging necesita de Customers, por un puerto de este módulo con
/// adaptador en Bootstrapper (<c>MessagingCustomerDirectory</c>). Messaging no referencia Customers.
/// </summary>
public interface ICustomerPhoneDirectory
{
    /// <summary>Un cliente por número (D-M7: el creado primero, luego el id menor). Una consulta por
    /// página; los números que no están no aparecen.</summary>
    Task<IReadOnlyDictionary<string, CustomerPhoneMatch>> MatchAsync(
        Guid tenantId, IReadOnlyCollection<string> e164, CancellationToken cancellationToken);

    /// <summary>Spec 2026-10-24 §4.1: todos los clientes del tenant con ese número —activos o no, completos o no—, el creado
    /// primero antes (<c>created_at, id</c>). Es la consulta de <see cref="MatchAsync"/> sin quedarse con el primero; el
    /// <paramref name="take"/> se aplica en SQL.</summary>
    Task<IReadOnlyList<CustomerPhoneMatch>> ListByPhoneAsync(Guid tenantId, string e164, int take, CancellationToken cancellationToken);

    /// <summary>Los <c>phone_e164</c> de los clientes cuyo nombre contiene <paramref name="term"/>
    /// (usa <c>IX_customers_name_trgm</c>), hasta <paramref name="cap"/>; con más, la búsqueda queda
    /// incompleta y se registra.</summary>
    Task<IReadOnlyList<string>> FindPhonesByNameAsync(Guid tenantId, string term, int cap, CancellationToken cancellationToken);
}
