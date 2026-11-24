using Microsoft.EntityFrameworkCore;
using Modules.Support.Domain;

namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-22 §7 and D-15/D-16: what the unassigned-alert worker reads and writes. Raw SQL on purpose: the heal and the claim must not
/// touch the ticket's <c>version</c> (nobody editing a ticket should ever get a 412 because of an alert), and the claim is an
/// <c>INSERT … ON CONFLICT DO NOTHING</c> that EF does not model. Every query filters by tenant.
/// </summary>
internal sealed class SupportUnassignedAlertStore(SupportDbContext dbContext)
{
    /// <summary>The tenants that have the alert on, with what the worker needs to count (the threshold, the schedule, the floor of the
    /// clock and the recipients).</summary>
    public async Task<IReadOnlyList<SupportTenantSettings>> ListEnabledAsync(CancellationToken cancellationToken) =>
        await dbContext.TenantSettings.AsNoTracking()
            .Where(settings => settings.UnassignedAlertEnabled)
            .OrderBy(settings => settings.TenantId)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// D-15: the markers a pod from before this change left wrong during the rollout. Tickets without an assignee, in a status that counts and
    /// with a null <c>unassigned_since</c> get it stamped with <paramref name="now"/>; tickets that ARE assigned but still carry a marker (an
    /// old pod assigned them without clearing it) get it cleared, so a later unassignment by an old pod cannot inherit a stale stretch (an
    /// immediate alert, or one that never comes). Plain <c>UPDATE</c>s that do not touch <c>version</c> nor <c>updated_at</c>: it is
    /// bookkeeping, not an edit. Returns how many rows it fixed. An internal task is never «sin asignar» (it always has an assignee), so
    /// every statement of this store leaves <c>is_internal</c> rows out even if one lost its assignee by hand.
    /// </summary>
    public async Task<int> HealAsync(Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stamped = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE support.tickets
            SET unassigned_since = {now.ToUniversalTime()}
            WHERE tenant_id = {tenantId}
              AND assignee_member_id IS NULL
              AND NOT is_internal
              AND status IN ('New', 'Open', 'WaitingInternal')
              AND unassigned_since IS NULL
            """,
            cancellationToken);
        var cleared = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE support.tickets
            SET unassigned_since = NULL
            WHERE tenant_id = {tenantId}
              AND assignee_member_id IS NOT NULL
              AND unassigned_since IS NOT NULL
            """,
            cancellationToken);
        return stamped + cleared;
    }

    /// <summary>
    /// The oldest unassigned tickets of the tenant that have no claim for their current stretch and have been at it, on the wall clock, for
    /// at least <paramref name="notBefore"/> (business hours never exceed wall-clock hours, so the rest cannot have reached the
    /// threshold). Ordered by <c>unassigned_since</c>, oldest first: if one of them has not reached the threshold in business hours,
    /// no later one has either, which lets the worker stop at the first miss.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ListCandidateIdsAsync(
        Guid tenantId, DateTimeOffset notBefore, int take, CancellationToken cancellationToken) =>
        await dbContext.Database
            .SqlQuery<Guid>(
                $"""
                SELECT t.id AS "Value"
                FROM support.tickets t
                WHERE t.tenant_id = {tenantId}
                  AND t.assignee_member_id IS NULL
                  AND NOT t.is_internal
                  AND t.status IN ('New', 'Open', 'WaitingInternal')
                  AND t.unassigned_since IS NOT NULL
                  AND t.unassigned_since <= {notBefore.ToUniversalTime()}
                  AND NOT EXISTS (
                      SELECT 1 FROM support.unassigned_alerts a
                      WHERE a.ticket_id = t.id AND a.unassigned_since = t.unassigned_since)
                ORDER BY t.unassigned_since, t.id
                LIMIT {take}
                """)
            .ToListAsync(cancellationToken);

    /// <summary>The ticket, read without tracking: the worker only needs its ids and number to write the event.</summary>
    public Task<SupportTicket?> FindTicketAsync(Guid tenantId, Guid ticketId, CancellationToken cancellationToken) =>
        dbContext.Tickets.AsNoTracking()
            .SingleOrDefaultAsync(ticket => ticket.TenantId == tenantId && ticket.Id == new SupportTicketId(ticketId), cancellationToken);

    /// <summary>
    /// The claim of the alert of this stretch: <c>true</c> if this call inserted it, <c>false</c> if another replica (or an earlier tick) had.
    /// It must run in the transaction that also saves the event, so a claim never exists without its event (or without the note that there
    /// was nobody to tell). The insert selects from the ticket, so it only happens if the ticket is still unassigned, in a status that
    /// counts and in the same stretch: a ticket assigned or moved between the worker's read and this statement gets no claim and no event.
    /// </summary>
    public async Task<bool> TryClaimAsync(
        Guid tenantId,
        Guid ticketId,
        DateTimeOffset unassignedSince,
        int recipientCount,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO support.unassigned_alerts (ticket_id, unassigned_since, tenant_id, recipient_count, alerted_at)
            SELECT t.id, t.unassigned_since, t.tenant_id, {recipientCount}, {now.ToUniversalTime()}
            FROM support.tickets t
            WHERE t.id = {ticketId}
              AND t.tenant_id = {tenantId}
              AND t.assignee_member_id IS NULL
              AND NOT t.is_internal
              AND t.unassigned_since ={unassignedSince.ToUniversalTime()}
              AND t.status IN ('New', 'Open', 'WaitingInternal')
            ON CONFLICT (ticket_id, unassigned_since) DO NOTHING
            """,
            cancellationToken) == 1;
}
