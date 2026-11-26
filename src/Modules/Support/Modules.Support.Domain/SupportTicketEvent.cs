namespace Modules.Support.Domain;

/// <summary>Una entrada de la traza del ticket. El orden lo da <see cref="Ordinal"/>, que asigna el agregado, no
/// <see cref="OccurredAt"/>: los GUID v7 no ordenan dentro del mismo milisegundo (plan D10). <see cref="Details"/> es JSON
/// camelCase con <c>from</c>/<c>to</c> según <see cref="Kind"/>; los ids de miembros que guarda pueden ser de miembros ya
/// removidos.</summary>
public sealed class SupportTicketEvent
{
    // EF Core materializa por acá.
    private SupportTicketEvent()
    {
        Details = "{}";
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public SupportTicketId TicketId { get; private set; }

    public int Ordinal { get; private set; }

    public SupportTicketEventKind Kind { get; private set; }

    /// <summary>La membresía que actuó. Null queda reservado para acciones del sistema de fases siguientes.</summary>
    public Guid? ActorMemberId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string Details { get; private set; }

    internal static SupportTicketEvent Create(
        Guid tenantId, SupportTicketId ticketId, int ordinal, SupportTicketEventKind kind, Guid? actorMemberId,
        DateTimeOffset occurredAt, string details) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            TicketId = ticketId,
            Ordinal = ordinal,
            Kind = kind,
            ActorMemberId = actorMemberId,
            OccurredAt = occurredAt,
            Details = details,
        };
}
