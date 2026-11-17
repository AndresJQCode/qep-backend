namespace Modules.Messaging.Application;

/// <summary>Soporte fase 2: el número visible (<c>SOP-000127</c>) de los tickets que nombran los eventos <c>TicketOpened</c> y
/// <c>TicketClosed</c> del hilo. Puerto de Messaging que implementa el Bootstrapper leyendo <c>SupportDbContext</c>
/// (<c>Bootstrapper/MessagingTicketNames.cs</c>): Messaging no referencia Support. Sólo los tickets del tenant; uno que no aparece en el
/// diccionario sale en el hilo sin número.</summary>
public interface IMessagingTicketNames
{
    Task<IReadOnlyDictionary<Guid, string>> FindNumbersAsync(Guid tenantId, IReadOnlyCollection<Guid> ticketIds, CancellationToken ct);
}
