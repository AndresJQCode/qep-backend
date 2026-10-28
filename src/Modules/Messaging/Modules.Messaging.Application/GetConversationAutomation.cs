using System.Text.Json;
using BuildingBlocks.Application;
using Modules.Messaging.Domain;
using Modules.Tenancy.Application;

namespace Modules.Messaging.Application;

public sealed record GetConversationAutomationQuery(Guid TenantId, Guid ConversationId) : IQuery<ConversationAutomationDetailsDto>;

/// <param name="FailureCode">Spec 2026-10-24 D-T8 (BFF): el código por el que falló la corrida, que el frontend traduce en «Motivo: …»;
/// <c>null</c> si no falló.</param>
public sealed record ConversationAutomationDetailsDto(
    Guid ChatbotId,
    string ChatbotName,
    int VersionNumber,
    string Status,
    string? StepTitle,
    JsonElement Variables,
    IReadOnlyList<ConversationAutomationTraceEntry> Trace,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? FailureCode);

/// <summary>Spec 2026-10-21 §5.5: «Datos capturados» del asesor. Sólo <c>messaging.conversation.read</c> (no el de Automations, que el
/// asesor no tiene, D-C10). 404 <c>messaging.conversation.not_found</c> si la conversación no es del tenant y 404
/// <c>messaging.automation.not_available</c> si nunca tuvo corrida; otro tenant ya es 403 antes de llegar acá.</summary>
public sealed class GetConversationAutomationHandler(
    IConversationQueries conversations,
    IConversationAutomationDetails details,
    ITenantModules tenantModules,
    IExecutionContext executionContext)
    : IQueryHandler<GetConversationAutomationQuery, ConversationAutomationDetailsDto>
{
    public async Task<ConversationAutomationDetailsDto> HandleAsync(GetConversationAutomationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await MessagingAuthorization.EnsureAsync(executionContext, tenantModules, query.TenantId, MessagingPermissions.ConversationRead, cancellationToken);
        _ = await conversations.FindAsync(query.TenantId, query.ConversationId, cancellationToken)
            ?? throw MessagingNotFound.Conversation(query.ConversationId);
        var found = await details.FindAsync(query.TenantId, query.ConversationId, cancellationToken)
            ?? throw new ResourceNotFoundException(
                MessagingErrorCodes.AutomationNotAvailable, $"Conversation '{query.ConversationId}' has no assistant run.");
        return new ConversationAutomationDetailsDto(
            found.ChatbotId, found.ChatbotName, found.VersionNumber, found.Status, found.StepTitle, found.Variables, found.Trace, found.StartedAt, found.EndedAt,
            found.FailureCode);
    }
}
