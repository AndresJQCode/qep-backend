using BuildingBlocks.Application;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;
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

/// <summary>Qué módulos le faltan al tenant para usar una plantilla. La misma regla alimenta la galería (<c>available</c>) y crear
/// (422), así que ninguna de las dos puede discrepar de la otra.</summary>
public static class AgentTemplateModules
{
    /// <summary>Las claves de <c>requires</c> que no están efectivas, en el orden de <c>requires</c>. Con <paramref name="modules"/> nulo
    /// (tenant sin fila: sólo pasa con el stub de desarrollo) no falta ninguna, como <c>TenantModuleGuard</c>, que en ese caso deja pasar.</summary>
    public static IReadOnlyList<TenantModuleKey> Missing(SystemAgentTemplate template, TenantModuleSet? modules)
    {
        ArgumentNullException.ThrowIfNull(template);
        return modules is null ? [] : [.. template.Requires.Where(key => !modules.IsEnabled(key))];
    }
}

/// <param name="Available">Calculado por el backend con los módulos del tenant: el frontend no lo deduce de la sesión.</param>
public sealed record AgentTemplateDto(string Id, string Name, string Description, IReadOnlyList<string> Requires, bool Available);

public sealed record ListAgentTemplatesQuery(Guid TenantId) : IQuery<IReadOnlyList<AgentTemplateDto>>;

/// <summary>La galería de plantillas. Exige <c>ai.agent.manage</c> y no <c>read</c>: sólo sirve para crear un agente.</summary>
public sealed class ListAgentTemplatesHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, ISystemAgentTemplateCatalog catalog)
    : IQueryHandler<ListAgentTemplatesQuery, IReadOnlyList<AgentTemplateDto>>
{
    public async Task<IReadOnlyList<AgentTemplateDto>> HandleAsync(ListAgentTemplatesQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentManage, cancellationToken);

        // Una lectura por request, no una por plantilla.
        var modules = await tenantModules.FindAsync(query.TenantId, cancellationToken);
        return
        [
            .. catalog.All.Select(template => new AgentTemplateDto(
                template.Id,
                template.Name,
                template.Description,
                [.. template.Requires.Select(key => key.Value)],
                AgentTemplateModules.Missing(template, modules).Count == 0)),
        ];
    }
}
