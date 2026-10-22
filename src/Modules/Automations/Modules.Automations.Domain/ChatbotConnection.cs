namespace Modules.Automations.Domain;

/// <summary>Spec 2026-10-21 §4.2 y D-C4: una conexión de Mensajería que el chatbot atiende. <see cref="ConnectionId"/> es una
/// referencia blanda a <c>integrations.connections</c>, sin FK entre esquemas. <see cref="Active"/> sólo en un chatbot publicado:
/// a lo sumo uno activo por conexión (<c>IX_chatbot_connections_active</c>).</summary>
public sealed class ChatbotConnection
{
    private ChatbotConnection()
    {
    }

    public Guid ChatbotId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public bool Active { get; private set; }
}
