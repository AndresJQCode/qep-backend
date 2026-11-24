using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>Support task notices: a read of the mirror, sibling of <see cref="WholesaleInvitationNoticeTemplateReadiness"/>. No cache: it goes
/// through the partial unique index <c>UX_message_templates_task_notice</c> (at most one <c>TaskNotice</c> row per connection).</summary>
internal sealed class TaskNoticeTemplateReadiness(MessagingDbContext dbContext) : ITaskNoticeTemplateReadiness
{
    public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == MessageTemplateKind.TaskNotice && template.Status == MessageTemplateStatuses.Approved)
            .Select(template => new ApprovedTemplate(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<string?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Kind == MessageTemplateKind.TaskNotice)
            .Select(template => (string?)template.Status)
            .FirstOrDefaultAsync(cancellationToken);
}
