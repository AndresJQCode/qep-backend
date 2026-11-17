using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.3: las delegaciones de un chatbot a un agente, siempre por <c>(tenant_id, id)</c>. Se
/// guardan con <see cref="IMessagingUnitOfWork"/>, en el mismo <c>SaveChanges</c> que el cambio de la conversación y la fila de la cola.</summary>
public interface IBotDelegationRepository
{
    /// <summary>Con tracking. <c>null</c> si no existe o es de otro tenant.</summary>
    Task<BotDelegation?> FindAsync(Guid tenantId, Guid delegationId, CancellationToken cancellationToken);

    /// <summary>Sólo agrega al contexto.</summary>
    void Add(BotDelegation delegation);
}
