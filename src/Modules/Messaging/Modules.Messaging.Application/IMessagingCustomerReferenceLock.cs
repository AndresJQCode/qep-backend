namespace Modules.Messaging.Application;

/// <summary>
/// Borrar clientes (spec 2026-11-10, A-12; plan P4): antes de escribir <c>customer_id</c> en una conversación, Mensajería toma el
/// candado compartido del cliente y comprueba que siga existiendo, dentro de la transacción que escribe. El borrado toma el mismo
/// candado exclusivo: si ganó la carrera, esto espera a su commit y devuelve <c>false</c>, y quien llama no cuelga nada del cliente
/// borrado. Exige una transacción abierta en el contexto de Mensajería: sin ella el candado se soltaría al terminar su sentencia.
/// </summary>
public interface IMessagingCustomerReferenceLock
{
    /// <returns><c>true</c> si el cliente existe en el tenant y queda protegido hasta el commit; <c>false</c> si ya no existe.</returns>
    Task<bool> HoldAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);
}
