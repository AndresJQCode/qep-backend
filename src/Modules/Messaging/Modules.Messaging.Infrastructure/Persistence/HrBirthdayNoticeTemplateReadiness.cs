using Microsoft.EntityFrameworkCore;
using Modules.Messaging.Application;
using Modules.Messaging.Domain;

namespace Modules.Messaging.Infrastructure.Persistence;

/// <summary>HR birthday notices: a read of the mirror, sibling of <see cref="TaskNoticeTemplateReadiness"/>. No cache: it goes through the partial
/// unique index <c>UX_message_templates_hr_birthday_notice</c> (at most one <c>HrBirthdayNotice</c> row per connection).</summary>
internal sealed class HrBirthdayNoticeTemplateReadiness(MessagingDbContext dbContext) : IHrBirthdayNoticeTemplateReadiness
{
    public Task<ApprovedTemplate?> FindApprovedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId
                && template.Kind == MessageTemplateKind.HrBirthdayNotice && template.Status == MessageTemplateStatuses.Approved)
            .Select(template => new ApprovedTemplate(template.Name, template.Language, template.Category, template.ParameterFormat, template.ComponentsJson))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<string?> FindStatusAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        dbContext.MessageTemplates.AsNoTracking()
            .Where(template => template.TenantId == tenantId && template.ConnectionId == connectionId && template.Kind == MessageTemplateKind.HrBirthdayNotice)
            .Select(template => (string?)template.Status)
            .FirstOrDefaultAsync(cancellationToken);
}
