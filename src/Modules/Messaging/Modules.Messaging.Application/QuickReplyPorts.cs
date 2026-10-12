using Modules.Messaging.Domain;

namespace Modules.Messaging.Application;

/// <summary>
/// Spec 2026-10-12 §3.1: las respuestas rápidas del tenant. Todo método recibe <c>tenantId</c>: el id de otro tenant responde igual que
/// uno inexistente. El agregado se escribe con tracking y se guarda con <see cref="IMessagingUnitOfWork"/>, que traduce el índice único
/// del atajo a <c>messaging.quick_reply.shortcut_taken</c> y lleva la auditoría en la misma transacción.
/// </summary>
public interface IQuickReplyRepository
{
    /// <summary>Con tracking.</summary>
    Task<QuickReply?> FindAsync(Guid tenantId, Guid quickReplyId, CancellationToken cancellationToken);

    /// <summary>D-Q7: todas, sin paginar, por <c>shortcut</c> en orden ordinal. Sin tracking.</summary>
    Task<IReadOnlyList<QuickReply>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    void Add(QuickReply quickReply);

    void Remove(QuickReply quickReply);
}
