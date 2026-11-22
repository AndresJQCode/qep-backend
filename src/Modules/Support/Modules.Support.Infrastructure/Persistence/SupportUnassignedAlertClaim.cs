namespace Modules.Support.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-11-22 D-16: the claim of one unassigned-ticket alert, <c>support.unassigned_alerts (ticket_id, unassigned_since)</c>. One per
/// ticket and per stretch without an assignee: a ticket that is unassigned again, or comes back to a status that counts, has another
/// <c>unassigned_since</c> and so another claim. The row is inserted with <c>ON CONFLICT DO NOTHING</c> in the same transaction as the outbox
/// event, so the ticket row is never touched and the alert never gives a 412 to whoever is editing the ticket. <c>RecipientCount = 0</c>
/// records that there was nobody to tell and that it is not retried.
/// <para>The worker writes it with SQL; this type exists so the model (and the migration) own the table.</para>
/// </summary>
internal sealed class SupportUnassignedAlertClaim
{
    public Guid TicketId { get; init; }

    public DateTimeOffset UnassignedSince { get; init; }

    public Guid TenantId { get; init; }

    public int RecipientCount { get; init; }

    public DateTimeOffset AlertedAt { get; init; }
}
