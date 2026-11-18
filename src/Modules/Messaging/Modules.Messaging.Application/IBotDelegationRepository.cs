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

    /// <summary>Revisión de la rama, hallazgo 3: pasa a <c>Interrupted</c> las delegaciones <c>Live</c> de la conversación y devuelve cuántas.
    /// Va dentro de la transacción que delega, antes de agregar la nueva: una viva que quedó colgada (un cambio que limpió la columna sin
    /// cerrarla) frenaba toda delegación siguiente por <c>IX_bot_delegations_live</c>. Corre en el acto (SQL), para que el índice ya no la vea
    /// cuando se inserte la nueva.</summary>
    Task<int> InterruptLiveAsync(Guid tenantId, Guid conversationId, DateTimeOffset now, CancellationToken cancellationToken);
}
