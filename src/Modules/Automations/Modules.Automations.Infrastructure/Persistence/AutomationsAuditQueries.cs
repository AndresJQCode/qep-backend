using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;

namespace Modules.Automations.Infrastructure.Persistence;

/// <summary>Lee <c>audit.entries</c> por el mismo <see cref="AutomationsDbContext"/> que las escribe (<see cref="AutomationsAuditRecorder"/>):
/// el filtro es el mismo trío fuente / tipo de recurso / acción con el que se registran.</summary>
internal sealed class AutomationsAuditQueries(AutomationsDbContext dbContext) : IAutomationsAuditQueries
{
    public async Task<DateTimeOffset?> LastDraftSavedAtAsync(Guid tenantId, Guid chatbotId, CancellationToken cancellationToken)
    {
        var resourceId = chatbotId.ToString();
        return await dbContext.AuditEntries.AsNoTracking()
            .Where(entry => entry.TenantId == tenantId
                && entry.Source == AutomationsAuditRecorder.Source
                && entry.ResourceType == AutomationsAuditActions.ResourceChatbot
                && entry.Action == AutomationsAuditActions.ChatbotDraftSaved
                && entry.ResourceId == resourceId)
            .MaxAsync(entry => (DateTimeOffset?)entry.OccurredAt, cancellationToken);
    }
}
