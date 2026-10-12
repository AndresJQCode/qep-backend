namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// La fila de <c>messaging.connection_send_budgets</c> (decisión del owner 2026-10-12, P3 y P5): el token bucket de una conexión, compartido por
/// todas las réplicas. Se lee y se escribe sólo por SQL (<see cref="Campaigns.ConnectionSendBudget"/>); el modelo de EF existe para el
/// esquema y la migración. <c>updated_at</c> es el último cambio del ritmo, no el último uso: de ahí cuenta la subida de 1 mps cada 30 s.
/// </summary>
internal sealed class ConnectionSendBudgetRecord
{
    public Guid ConnectionId { get; set; }

    public Guid TenantId { get; set; }

    public double Tokens { get; set; }

    public double Rate { get; set; }

    public DateTimeOffset RefilledAt { get; set; }

    public DateTimeOffset? ThrottledUntil { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
