namespace Modules.Support.Application;

/// <summary>
/// Spec 2026-11-15 §5.2 (plan N13): tells the app hub that a ticket changed, on <c>support:ticket:{id}</c> and on <c>support</c>. Every
/// write calls it explicitly after its commit and before returning, with <see cref="CancellationToken.None"/>; not from the unit of
/// work, because several writes confirm through a transaction the unit of work cannot see. It never throws.
/// </summary>
public interface ISupportRealtime
{
    Task TicketChangedAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken);

    /// <summary>Spec 2026-11-22 §5.4: a task changed. Same notice as <see cref="TicketChangedAsync"/> and, besides, one on
    /// <c>support:tasks:{m}</c> of each distinct member of <paramref name="memberIds"/> (creator, assignee, former assignee). Never
    /// throws. Handlers go through <see cref="SupportTicketNotices"/>, which picks this or the external one.</summary>
    Task TaskChangedAsync(Guid tenantId, Guid ticketId, IReadOnlyCollection<Guid> memberIds, CancellationToken cancellationToken);
}
