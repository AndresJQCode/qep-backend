using Modules.Support.Domain;

namespace Modules.Support.Application;

public interface ISupportTicketTypeRepository
{
    /// <summary>Los tipos externos del tenant por <c>Position</c>, con tracking. <b>Excluye el interno</b> (spec 2026-11-22): ajustes,
    /// reordenar, el reporte y el formulario público sólo ven los ocho; el interno se pide con <see cref="FindInternalAsync"/>.</summary>
    Task<IReadOnlyList<SupportTicketType>> ListAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>El tipo interno del tenant (uno solo), con tracking, o null si todavía no se sembró.</summary>
    Task<SupportTicketType?> FindInternalAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<SupportTicketType?> FindAsync(Guid tenantId, SupportTicketTypeId id, CancellationToken cancellationToken);

    void Add(SupportTicketType type);

    /// <summary>Fase 4a (plan G4): el borrado se aplica al guardar. Si un ticket lo usa, la FK <c>RESTRICT</c> lo frena y el unit of
    /// work responde <c>support.ticket_type.in_use</c>.</summary>
    void Remove(SupportTicketType type);
}
