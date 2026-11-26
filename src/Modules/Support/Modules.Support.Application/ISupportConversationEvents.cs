namespace Modules.Support.Application;

/// <summary>
/// Los eventos del hilo que escribe Soporte (plan E11): <c>TicketOpened</c> al crear un ticket desde la conversación o al vincular una
/// conversación nueva por plantilla, y <c>TicketClosed</c> al cerrarlo. El adaptador de Bootstrapper enrola Mensajería en la conexión y
/// la transacción abierta de Soporte, así que no queda un ticket sin su evento ni al revés.
/// </summary>
public interface ISupportConversationEvents
{
    /// <summary>Exige la transacción de <see cref="ISupportUnitOfWork.BeginTransactionAsync"/>; sin ella, lanza. Una conversación que ya
    /// no existe no escribe nada. <paramref name="opened"/> elige el tipo: verdadero <c>TicketOpened</c>, falso <c>TicketClosed</c>.</summary>
    Task AppendAsync(
        Guid tenantId,
        Guid conversationId,
        bool opened,
        Guid ticketId,
        Guid? actorMemberId,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Avisa al canal en tiempo real de Mensajería que el hilo cambió, para que la bandeja vuelva a pedirlo. Se llama
    /// <b>después</b> de confirmar y es de mejor esfuerzo: nunca lanza.</summary>
    Task NotifyChangedAsync(Guid tenantId, Guid conversationId, CancellationToken cancellationToken);
}
