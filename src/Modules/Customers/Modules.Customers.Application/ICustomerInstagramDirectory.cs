namespace Modules.Customers.Application;

/// <summary>Quien escribió por Instagram, como lo ve Customers: IGSID, usuario y nombre del perfil si Meta los dio (V-7).</summary>
public sealed record InstagramContact(string UserId, string? Username, string? Name);

/// <summary>
/// Spec 2026-10-14 decisión 10: lo que Messaging necesita de Customers para atar una conversación de Instagram a un cliente, con
/// adaptador en Bootstrapper (<c>MessagingCustomerDirectory</c>). Messaging no referencia Customers. Sin emparejar por teléfono: no viene.
/// </summary>
public interface ICustomerInstagramDirectory
{
    /// <summary>Por IGSID → incompleto nuevo. Idempotente: un reintento encuentra el cliente por IGSID. Dos llamadas concurrentes con el
    /// mismo IGSID nuevo devuelven el mismo cliente. Si el cliente ya existe y no tenía usuario, se le anota el que llegó.</summary>
    Task<EnsuredCustomer> EnsureAsync(Guid tenantId, InstagramContact contact, CancellationToken cancellationToken);
}
