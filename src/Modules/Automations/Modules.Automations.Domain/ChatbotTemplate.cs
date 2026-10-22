namespace Modules.Automations.Domain;

/// <summary>Spec 2026-10-21 §4.2 y §4.5: una plantilla del tenant (las del sistema viven en código). Acá sólo las propiedades que
/// EF mapea; el comportamiento llega con la tarea 17 del plan.</summary>
public sealed class ChatbotTemplate
{
    public const int NameMaxLength = 80;
    public const int CategoryMaxLength = 24;
    public const int DescriptionMaxLength = 300;

    private ChatbotTemplate()
    {
        Name = string.Empty;
        Category = string.Empty;
        Description = string.Empty;
        FlowJson = "{}";
        LayoutJson = "{}";
        ParametersJson = "[]";
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public string Category { get; private set; }

    public string Description { get; private set; }

    public string FlowJson { get; private set; }

    public string LayoutJson { get; private set; }

    /// <summary>Los huecos que se llenan al crear desde la plantilla (D-C8).</summary>
    public string ParametersJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid CreatedBy { get; private set; }
}
