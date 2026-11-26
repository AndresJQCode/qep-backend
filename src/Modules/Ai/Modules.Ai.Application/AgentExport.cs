using BuildingBlocks.Application;
using FluentValidation;
using Modules.Ai.Domain;
using Modules.Tenancy.Application;

namespace Modules.Ai.Application;

// Spec 2026-11-17 importar/exportar: un agente viaja en un archivo a otro tenant o a otro ambiente. El archivo lleva el borrador sin lo que
// es del tenant (en el destino esos ids no significan nada, o significan otra cosa) y al importarlo nace un agente nuevo en borrador, sin
// conexiones. Lo que falta para publicar lo marca el panel de problemas del editor. Leer y escribir el archivo exige ai.agent.manage, como
// los chatbots.

/// <summary>El archivo de un agente. <see cref="Schema"/> es la versión del archivo; <see cref="Kind"/> lo distingue de un archivo de
/// chatbot, que también trae <c>schema: 1</c>. <see cref="Config"/> es el borrador, ya pasado por <see cref="AgentExport.Portable"/>.</summary>
public sealed record AgentExportDto(int Schema, string Kind, string Name, AgentConfig Config, DateTimeOffset ExportedAt)
{
    public const int CurrentSchema = 1;
    public const string KindValue = "aiAgent";
}

public static class AgentExport
{
    /// <summary>Quita lo que es del tenant: el miembro del traspaso (el destino <c>member</c> se queda, así el editor pide elegir a alguien
    /// en vez de mandar las conversaciones en silencio a la bandeja sin asignar) y el asesor y la cuenta de los borradores de cotización.
    /// Se aplica al exportar y otra vez al importar, por si el archivo se editó a mano.</summary>
    public static AgentConfig Portable(AgentConfig config) =>
        config with { Handoff = config.Handoff with { MemberId = null }, QuotationDraft = null };
}

public sealed record ExportAgentQuery(Guid TenantId, Guid AgentId) : IQuery<AgentExportDto>;

public sealed class ExportAgentHandler(
    IExecutionContext executionContext, ITenantModules tenantModules, IAgentRepository agents, IClock clock)
    : IQueryHandler<ExportAgentQuery, AgentExportDto>
{
    public async Task<AgentExportDto> HandleAsync(ExportAgentQuery query, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AiPermissions.AgentManage, cancellationToken);
        var agent = await agents.FindAsync(query.TenantId, query.AgentId, cancellationToken) ?? throw AiNotFound.Agent(query.AgentId);
        return new AgentExportDto(
            AgentExportDto.CurrentSchema, AgentExportDto.KindValue, agent.Name, AgentExport.Portable(agent.DraftConfig), clock.UtcNow);
    }
}

/// <param name="Name">El que se eligió al importar o, sin él, el del archivo; lo resuelve el endpoint.</param>
public sealed record ImportAgentCommand(Guid TenantId, string Name, AgentExportDto Export) : ICommand<AgentEditorDto>;

/// <summary>Un archivo de chatbot (sin <c>kind</c>) o de otra versión es 422, no un agente vacío. El <c>config</c> sigue los topes de
/// guardar el borrador, con las claves bajo <c>Export.Config.</c>.</summary>
public sealed class ImportAgentValidator : AbstractValidator<ImportAgentCommand>
{
    public ImportAgentValidator()
    {
        AgentRules.Name(RuleFor(command => command.Name));
        RuleFor(command => command.Export).NotNull();
        When(command => command.Export is not null, () =>
        {
            RuleFor(command => command.Export.Schema).Equal(AgentExportDto.CurrentSchema)
                .WithMessage($"Only schema {AgentExportDto.CurrentSchema} files can be imported.");
            RuleFor(command => command.Export.Kind).Equal(AgentExportDto.KindValue)
                .WithMessage("The file is not an AI agent export.");
            RuleFor(command => command.Export.Config).NotNull().SetValidator(new AgentConfigFieldsValidator());
        });
    }
}

/// <summary>Como crear un agente en blanco, con el <c>config</c> del archivo: nace en borrador, sin conexiones, y se audita como creado.</summary>
public sealed class ImportAgentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AiCaller caller,
    IAgentRepository agents,
    IAiUnitOfWork unitOfWork,
    IAiAuditRecorder audit,
    IClock clock,
    IValidator<ImportAgentCommand> validator,
    AgentEditorBuilder builder)
    : ICommandHandler<ImportAgentCommand, AgentEditorDto>
{
    public async Task<AgentEditorDto> HandleAsync(ImportAgentCommand command, CancellationToken cancellationToken)
    {
        await AiAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AiPermissions.AgentManage, cancellationToken);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);
        var name = command.Name.Trim();
        await CreateAgentHandler.EnsureNameIsFreeAsync(agents, command.TenantId, name, null, cancellationToken);

        var now = clock.UtcNow;
        var agent = Agent.Create(command.TenantId, name, AgentExport.Portable(command.Export.Config), by, now);
        agents.Add(agent);
        audit.Record(command.TenantId, executionContext.SubjectId, AiAuditActions.AgentCreated, agent.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await builder.BuildAsync(agent, cancellationToken);
    }
}
