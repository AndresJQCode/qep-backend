namespace Modules.Automations.Domain;

/// <summary>Spec 2026-10-21 §4.2 y D-C4: una conexión de Mensajería que el chatbot atiende. <see cref="ConnectionId"/> es una
/// referencia blanda a <c>integrations.connections</c>, sin FK entre esquemas. <see cref="Active"/> sólo en un chatbot publicado:
/// a lo sumo uno activo por conexión (<c>IX_chatbot_connections_active</c>).</summary>
public sealed class ChatbotConnection
{
    private ChatbotConnection()
    {
    }

    internal static ChatbotConnection Create(Guid chatbotId, Guid tenantId, Guid connectionId, bool active) =>
        new() { ChatbotId = chatbotId, TenantId = tenantId, ConnectionId = connectionId, Active = active };

    /// <summary>Publicar ocupa la conexión (D-C13).</summary>
    internal void Activate() => Active = true;

    public Guid ChatbotId { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ConnectionId { get; private set; }

    public bool Active { get; private set; }
}
