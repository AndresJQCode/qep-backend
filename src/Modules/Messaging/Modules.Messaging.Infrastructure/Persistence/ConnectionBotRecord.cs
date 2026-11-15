namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>
/// Spec 2026-10-29 §4.3 (agentes IA): quién atiende una conexión de Mensajería, chatbot o agente. La clave primaria es la conexión, así
/// que una conexión tiene a lo sumo un bot: es lo único que hace atómico «una conexión, un solo bot» entre dos módulos que no se ven
/// (Automations y Ai). Mensajería es dueña de la tabla porque es lo único que los dos usan; cada uno la escribe en su propia
/// transacción, por SQL en Bootstrapper (<c>ChatbotConnectionSlots</c>). <c>connection_id</c> es referencia blanda a
/// <c>integrations.connections</c> y <c>bot_id</c> a <c>automations.chatbots</c> o <c>ai.agents</c>, sin FK entre esquemas.
/// </summary>
internal sealed class ConnectionBotRecord
{
    public const short Chatbot = 1;
    public const short Agent = 2;

    public Guid ConnectionId { get; set; }
    public Guid TenantId { get; set; }

    /// <summary>1 chatbot, 2 agente.</summary>
    public short BotKind { get; set; }

    public Guid BotId { get; set; }

    /// <summary>Copia del nombre del bot, para el 409 y la bandeja sin leer el esquema del otro módulo.</summary>
    public string BotName { get; set; } = string.Empty;

    public DateTimeOffset ReservedAt { get; set; }
}
