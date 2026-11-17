namespace Modules.Support.Domain;

/// <summary>La prioridad del ticket (spec 2026-11-15): cuatro niveles fijos, guardados por nombre. Ordena la bandeja; no toca el
/// reloj ni el vencimiento.</summary>
public enum SupportTicketPriority
{
    Low,
    Medium,
    High,
    Urgent,
}
