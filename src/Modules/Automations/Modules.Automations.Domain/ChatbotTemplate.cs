using System.Collections.Frozen;

namespace Modules.Automations.Domain;

/// <summary>Spec 2026-10-21 §4.2, §4.5 y D-C8: una plantilla del tenant, guardada desde el borrador de un chatbot (las del sistema son
/// JSON embebido en Infrastructure). Crear un chatbot desde ella copia flujo y posiciones; nada queda enlazado.</summary>
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

    /// <summary>El validador ya dio el 422 con su campo (nombre 1..80, categoría de <see cref="ChatbotTemplateCategories"/>,
    /// descripción hasta 300); acá sólo se recorta.</summary>
    public static ChatbotTemplate Create(
        Guid tenantId, string name, string category, string description, string flowJson, string layoutJson, Guid by, DateTimeOffset now) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Name = name.Trim(),
            Category = category,
            Description = description.Trim(),
            FlowJson = flowJson,
            LayoutJson = layoutJson,
            // D-C14: una plantilla del tenant no declara parámetros; sólo las del sistema los traen.
            ParametersJson = "[]",
            CreatedAt = now,
            CreatedBy = by,
        };

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

/// <summary>Las categorías de la galería, las mismas del frontend (qep-frontend, <c>features/automations/types/automations.ts</c>).</summary>
public static class ChatbotTemplateCategories
{
    public static readonly IReadOnlySet<string> All =
        new[] { "Support", "Sales", "Orders", "TechnicalService" }.ToFrozenSet(StringComparer.Ordinal);
}
