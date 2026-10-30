using Modules.Billing.Domain;

namespace Modules.Billing.Infrastructure.Persistence;

// Entidades de infraestructura (spec 2026-10-29 §2): el worker de Task 10 es el único que las
// escribe, y la lista de cobros las lee sólo para armar `lastReminder`. No tienen comportamiento de
// dominio, por eso son registros planos y no agregados con fábrica.

internal enum CustomerMessageKind
{
    Reminder = 1,
    Overdue = 2,
}

internal enum MessageDeliveryStatus
{
    Sending = 1,
    Sent = 2,
    Failed = 3,
}

/// <summary>El mensaje del día a un cliente; <c>(tenant_id, customer_id, run_date)</c> único es su deduplicación.</summary>
internal sealed class CustomerMessage
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid CustomerId { get; set; }

    public DateOnly RunDate { get; set; }

    public CustomerMessageKind Kind { get; set; }

    /// <summary>El canal del perfil al momento de enviar, no el de hoy: el perfil puede cambiar después.</summary>
    public BillingChannel Channel { get; set; }

    public MessageDeliveryStatus Status { get; set; }

    public string? FailureCode { get; set; }

    /// <summary>Lease de 5 min: una fila <c>Sending</c> vencida la retoma otra réplica.</summary>
    public DateTimeOffset ClaimedUntil { get; set; }

    public DateTimeOffset AttemptedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Qué paso de qué cobro cubrió cada mensaje; la PK <c>(charge_id, step)</c> hace que un paso se cubra una sola vez.</summary>
internal sealed class ReminderStep
{
    public Guid ChargeId { get; set; }

    public string Step { get; set; } = string.Empty;

    public Guid MessageId { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>El registro del email diario al equipo: una fila por encargado y día es su deduplicación.</summary>
internal sealed class DailyDigest
{
    public Guid TenantId { get; set; }

    public Guid MemberId { get; set; }

    public DateOnly RunDate { get; set; }

    public MessageDeliveryStatus Status { get; set; }

    public string? FailureCode { get; set; }

    public DateTimeOffset ClaimedUntil { get; set; }

    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>La corrida diaria de un tenant: con <c>FinishedAt</c> un tick no hace nada; con el lease vencido otra réplica la retoma.</summary>
internal sealed class TenantRun
{
    public Guid TenantId { get; set; }

    public DateOnly RunDate { get; set; }

    public DateTimeOffset ClaimedUntil { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }
}
