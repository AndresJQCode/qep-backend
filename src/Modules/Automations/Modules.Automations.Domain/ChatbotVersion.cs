namespace Modules.Automations.Domain;

/// <summary>Spec 2026-10-21 §4.2: una publicación del chatbot. Inmutable: una corrida viva sigue en la versión con la que empezó
/// aunque se publique otra.</summary>
public sealed class ChatbotVersion
{
    private ChatbotVersion()
    {
        FlowJson = "{}";
        LayoutJson = "{}";
    }

    public Guid Id { get; private set; }

    public Guid ChatbotId { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>1, 2, 3… por chatbot (único con <see cref="ChatbotId"/>).</summary>
    public int Number { get; private set; }

    public string FlowJson { get; private set; }

    public string LayoutJson { get; private set; }

    public DateTimeOffset PublishedAt { get; private set; }

    public Guid PublishedBy { get; private set; }
}
