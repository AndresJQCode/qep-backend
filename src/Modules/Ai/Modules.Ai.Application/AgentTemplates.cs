using Modules.Ai.Domain;
using Modules.Tenancy.Domain;

namespace Modules.Ai.Application;

// Spec 2026-11-23 (plantillas de agente IA): las plantillas del sistema, JSON embebido en Infrastructure, calcadas de las de chatbots.

/// <summary>Una plantilla de agente del sistema tal como la trae su archivo. <see cref="ConfigJson"/> es el <c>config</c> crudo, sin
/// <c>model</c>: el proveedor y el modelo los elige el tenant al crear (<see cref="ConfigFor"/>).</summary>
/// <param name="Requires">Los módulos que el tenant necesita para usarla; <c>available</c> sale de ellos.</param>
public sealed record SystemAgentTemplate(
    string Id, string Name, string Description, IReadOnlyList<TenantModuleKey> Requires, string ConfigJson)
{
    public const int IdMaxLength = 64;
    public const int DescriptionMaxLength = 300;

    /// <summary>El <c>config</c> del agente nuevo: lo de la plantilla más el modelo predeterminado que resolvió quien crea.</summary>
    public AgentConfig ConfigFor(AgentModelRef model) => AgentConfig.FromJson(ConfigJson) with { Model = model };
}

/// <summary>Las plantillas de agente del sistema, cargadas una vez al arrancar desde los recursos embebidos de Infrastructure.</summary>
public interface ISystemAgentTemplateCatalog
{
    IReadOnlyList<SystemAgentTemplate> All { get; }

    SystemAgentTemplate? Find(string id);
}
