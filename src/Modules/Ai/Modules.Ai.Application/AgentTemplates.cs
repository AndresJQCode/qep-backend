using BuildingBlocks.Application;
using FluentValidation;
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

public sealed record CreateAgentFromTemplateCommand(Guid TenantId, string TemplateId, string Name) : ICommand<AgentEditorDto>;

public sealed class CreateAgentFromTemplateValidator : AbstractValidator<CreateAgentFromTemplateCommand>
{
    public CreateAgentFromTemplateValidator()
    {
        AgentRules.Name(RuleFor(command => command.Name));
        RuleFor(command => command.TemplateId).NotEmpty().MaximumLength(SystemAgentTemplate.IdMaxLength);
    }
}

/// <summary>Nace como un agente en blanco (borrador, sin conexiones, modelo predeterminado) pero con el <c>config</c> de la plantilla,
/// copiado: cambiar la plantilla después no toca los agentes ya creados. Los módulos se revalidan acá y no se confía en lo que la
/// galería mostró: una pestaña vieja puede pedir una plantilla que el tenant ya no puede usar, y un agente que no se puede publicar
/// sería peor que el 422.</summary>
public sealed class CreateAgentFromTemplateHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AiCaller caller,
    IAgentRepository agents,
    IAiModelCatalog catalog,
    ISystemAgentTemplateCatalog templates,
    IAiUnitOfWork unitOfWork,
    IAiAuditRecorder audit,
    IClock clock,
    IValidator<CreateAgentFromTemplateCommand> validator,
    AgentEditorBuilder builder)
    : ICommandHandler<CreateAgentFromTemplateCommand, AgentEditorDto>
{
    public async Task<AgentEditorDto> HandleAsync(CreateAgentFromTemplateCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var template = templates.Find(command.TemplateId) ?? throw AiNotFound.Template(command.TemplateId);
        var missing = AgentTemplateModules.Missing(template, await tenantModules.FindAsync(command.TenantId, cancellationToken));
        if (missing.Count > 0)
        {
            throw new AiDomainException(
                AiErrorCodes.TemplateModuleNotEnabled,
                $"La plantilla necesita módulos que no están activos: {string.Join(", ", missing)}.");
        }

        var by = await caller.RequireAsync(command.TenantId, cancellationToken);
        var name = command.Name.Trim();
        await CreateAgentHandler.EnsureNameIsFreeAsync(agents, command.TenantId, name, null, cancellationToken);

        var now = clock.UtcNow;
        var agent = Agent.Create(command.TenantId, name, template.ConfigFor(ModelCatalog.DefaultFor(catalog)), by, now, sourceTemplateId: template.Id);
        agents.Add(agent);
        audit.Record(command.TenantId, executionContext.SubjectId, AiAuditActions.AgentCreated, agent.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await builder.BuildAsync(agent, cancellationToken);
    }
}
