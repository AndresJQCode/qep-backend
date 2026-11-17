using BuildingBlocks.Application;
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
