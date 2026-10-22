using Microsoft.EntityFrameworkCore;
using Modules.Automations.Application;

namespace Modules.Automations.Infrastructure.Persistence;

/// <summary>Lee <c>audit.entries</c> por el mismo <see cref="AutomationsDbContext"/> que las escribe (<see cref="AutomationsAuditRecorder"/>):
/// el filtro es el mismo trío fuente / tipo de recurso / acción con el que se registran.</summary>
internal sealed class AutomationsAuditQueries(AutomationsDbContext dbContext) : IAutomationsAuditQueries
{
    // El único índice útil de audit.entries es (tenant_id, occurred_at): sin acotar por tiempo, cada autoguardado recorrería toda la
    // historia del tenant filtrando fila por fila. Con «desde» el índice solo lee el último minuto.
    public Task<bool> HasDraftSavedSinceAsync(Guid tenantId, Guid chatbotId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var resourceId = chatbotId.ToString();
        var from = since.ToUniversalTime();
        return dbContext.AuditEntries.AsNoTracking().AnyAsync(
            entry => entry.TenantId == tenantId
                && entry.OccurredAt >= from
                && entry.Source == AutomationsAuditRecorder.Source
                && entry.ResourceType == AutomationsAuditActions.ResourceChatbot
                && entry.Action == AutomationsAuditActions.ChatbotDraftSaved
                && entry.ResourceId == resourceId,
            cancellationToken);
    }
}
