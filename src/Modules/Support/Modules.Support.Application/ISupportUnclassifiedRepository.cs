using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>Los entrantes sin clasificar (spec §8.2, plan E5). Registrar dos veces el mismo mensaje choca con la PK
/// <c>PK_unclassified_messages</c> al guardar: <c>support.unclassified.already_recorded</c>.</summary>
public interface ISupportUnclassifiedRepository
{
    /// <summary>Con tracking; pendiente o ya resuelto.</summary>
    Task<SupportUnclassifiedMessage?> FindAsync(Guid tenantId, Guid messageId, CancellationToken cancellationToken);

    /// <summary>Los pendientes de la conversación, por la hora del mensaje. Con tracking: el seguimiento los resuelve.</summary>
    Task<IReadOnlyList<SupportUnclassifiedMessage>> ListPendingAsync(
        Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    Task<int> CountPendingAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);

    void Add(SupportUnclassifiedMessage message);
}
