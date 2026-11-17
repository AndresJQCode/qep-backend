namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Spec 2026-10-21 §4.2: un entrante pendiente de que el motor de chatbots lo procese. La ingesta lo inserta en la misma
/// transacción que el mensaje; clave por <c>message_id</c> para que reintentar la ingesta no lo duplique.</summary>
internal sealed class AutomationInboxRecord
{
    public Guid MessageId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid ConversationId { get; set; }

    /// <summary>D-M16: este entrante abrió o reabrió la conversación.</summary>
    public bool Opened { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public short Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Outcome { get; set; }

    /// <summary>Spec 2026-11-03: <see cref="Modules.Messaging.Application.AutomationInboxKinds"/>; las filas anteriores son <c>Engine</c>.</summary>
    public string Kind { get; set; } = Modules.Messaging.Application.AutomationInboxKinds.Engine;

    /// <summary>Spec 2026-10-29 §6.1: qué procesador la reclama (smallint de <see cref="Domain.BotKind"/>: 1 el motor de chatbots, 2 el de
    /// agentes). Las filas anteriores son de chatbot.</summary>
    public short BotKind { get; set; } = (short)Domain.BotKind.Chatbot;

    /// <summary>Spec 2026-11-17 (agente IA en chatbots) §5.3: la delegación de una fila <c>Delegated</c> o <c>AgentReturned</c>; <c>null</c> en
    /// las demás (<c>CK_automation_inbox_delegation</c>).</summary>
    public Guid? DelegationId { get; set; }
}
