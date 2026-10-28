namespace Modules.Identity.Application;

/// <summary>
/// Escribe un evento en el Outbox de plataforma. No guarda: la fila viaja en el
/// <see cref="IIdentityUnitOfWork"/> del llamador, atómica con el resto de su escritura.
/// </summary>
public interface IIdentityOutbox
{
    void Add(string eventName, object payload, string correlationId, DateTimeOffset occurredAt);
}
