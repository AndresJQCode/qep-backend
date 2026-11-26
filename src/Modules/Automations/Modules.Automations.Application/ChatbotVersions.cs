using BuildingBlocks.Application;
using Modules.Automations.Domain.Flow;
using Modules.Tenancy.Application;

namespace Modules.Automations.Application;

// Spec 2026-11-18 (versiones del chatbot): el historial de publicaciones y devolver una al borrador.

public sealed record ListChatbotVersionsQuery(Guid TenantId, Guid ChatbotId) : IQuery<ChatbotVersionListResponse>;

public sealed record RestoreChatbotVersionCommand(Guid TenantId, Guid ChatbotId, int Number, long Version) : ICommand<ChatbotEditorDto>;

/// <summary>Las publicaciones del chatbot, la más nueva primero. El agregado ya trae todas sus versiones, así que no hace falta una
/// consulta aparte. Un flujo que ya no se puede leer cuenta 0 bloques: el historial no debe fallar por un dato viejo.</summary>
public sealed class ListChatbotVersionsHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IChatbotRepository chatbots,
    IAutomationMembers members)
    : IQueryHandler<ListChatbotVersionsQuery, ChatbotVersionListResponse>
{
    public async Task<ChatbotVersionListResponse> HandleAsync(ListChatbotVersionsQuery query, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, AutomationsPermissions.ChatbotRead, cancellationToken);
        var chatbot = await chatbots.FindAsync(query.TenantId, query.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(query.ChatbotId);
        var names = (await members.ListAsync(query.TenantId, cancellationToken)).ToDictionary(member => member.Id, member => member.Name);

        return new ChatbotVersionListResponse([.. chatbot.Versions
            .OrderByDescending(version => version.Number)
            .Select(version => new ChatbotVersionDto(
                version.Number,
                version.PublishedAt,
                version.PublishedBy,
                names.GetValueOrDefault(version.PublishedBy),
                version.Id == chatbot.PublishedVersionId,
                BlockCount(version.FlowJson)))]);
    }

    private static int BlockCount(string flowJson)
    {
        try
        {
            return FlowJson.Parse(flowJson).Nodes.Count;
        }
        catch (FlowFormatException)
        {
            return 0;
        }
    }
}

/// <summary>Copia el flujo y las posiciones de una versión al borrador. Sólo toca el borrador: el estado, la versión viva y las
/// conexiones no cambian. Guarda siempre, como D-C3: una versión vieja puede apuntar a algo que ya no existe y eso se muestra como
/// <c>issues</c>.</summary>
public sealed class RestoreChatbotVersionHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    AutomationsCaller caller,
    IChatbotRepository chatbots,
    IAutomationsUnitOfWork unitOfWork,
    IAutomationsAuditRecorder audit,
    IClock clock,
    ChatbotEditorBuilder builder)
    : ICommandHandler<RestoreChatbotVersionCommand, ChatbotEditorDto>
{
    public async Task<ChatbotEditorDto> HandleAsync(RestoreChatbotVersionCommand command, CancellationToken cancellationToken)
    {
        await AutomationsAuthorization.EnsureAsync(executionContext, tenantModules, command.TenantId, AutomationsPermissions.ChatbotManage, cancellationToken);
        var chatbot = await chatbots.FindAsync(command.TenantId, command.ChatbotId, cancellationToken)
            ?? throw AutomationsNotFound.Chatbot(command.ChatbotId);
        AutomationsVersions.Ensure(chatbot.Version, command.Version);
        var version = chatbot.Versions.FirstOrDefault(candidate => candidate.Number == command.Number)
            ?? throw AutomationsNotFound.ChatbotVersion(command.Number);
        var by = await caller.RequireAsync(command.TenantId, cancellationToken);

        var now = clock.UtcNow;
        chatbot.SaveDraft(version.FlowJson, version.LayoutJson, by, now);
        audit.Record(command.TenantId, executionContext.SubjectId, false, AutomationsAuditActions.ChatbotVersionRestored, AutomationsAuditActions.ResourceChatbot, chatbot.Id, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await builder.BuildAsync(chatbot, cancellationToken);
    }
}
