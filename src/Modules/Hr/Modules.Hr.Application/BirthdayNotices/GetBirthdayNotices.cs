using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.BirthdayNotices;

public sealed record GetBirthdayNoticesQuery(Guid TenantId) : IQuery<BirthdayNoticesDto>;

/// <summary>Builds the DTO of the setting for both the GET and the PUT: each manager with the flags the screen warns with, and
/// (spec 2026-11-27) the candidates and the WhatsApp connections the screen offers.</summary>
public sealed class BirthdayNoticesComposer(
    IHrMemberDirectory members, IHrMemberContacts contacts, IHrWhatsAppConnections whatsAppConnections)
{
    /// <param name="includeCandidates"><c>false</c> sends <c>candidates: null</c>: the caller cannot edit the setting.</param>
    public async Task<BirthdayNoticesDto> ComposeAsync(
        Guid tenantId, BirthdayNoticeSetting? setting, bool includeCandidates, CancellationToken cancellationToken)
    {
        var ids = setting?.ManagerMemberIds ?? [];

        // Asked once: the candidates and hasHrAccess are the same set, the one the PUT accepts as managers.
        var readers = includeCandidates || ids.Count > 0
            ? await members.ListWithPermissionAsync(tenantId, HrPermissions.EmployeeRead, cancellationToken)
            : [];
        var candidates = includeCandidates
            ? readers.Select(member => new BirthdayCandidateDto(member.MemberId, member.DisplayName)).ToList()
            : null;
        var connections = (await whatsAppConnections.ListAsync(tenantId, cancellationToken))
            .Select(connection => new BirthdayConnectionDto(
                connection.ConnectionId, connection.Name, connection.PhoneNumber, connection.TemplateStatus))
            .ToList();

        if (setting is null)
        {
            return new BirthdayNoticesDto(
                [], BirthdayNoticeSetting.DefaultLeadDays, false, null, false, null, candidates, connections);
        }

        var found = (await members.FindAsync(tenantId, ids, cancellationToken)).ToDictionary(member => member.MemberId);
        var withAccess = readers.Select(member => member.MemberId).ToHashSet();
        var reachable = await contacts.FindAsync(tenantId, ids, cancellationToken);

        // The order the PUT sent: the screen repaints the list as it saved it. A member that no longer exists is left out.
        var managers = new List<BirthdayManagerDto>(ids.Count);
        foreach (var id in ids)
        {
            if (!found.TryGetValue(id, out var member))
            {
                continue;
            }

            var contact = reachable.GetValueOrDefault(id, new HrMemberContact(false, false));
            managers.Add(new BirthdayManagerDto(
                id, member.DisplayName, contact.HasPhone, contact.HasEmail, member.IsActive && withAccess.Contains(id)));
        }

        return new BirthdayNoticesDto(
            managers, setting.LeadDays, setting.WhatsAppEnabled, setting.WhatsAppConnectionId, setting.EmailEnabled, setting.Version,
            candidates, connections);
    }
}

public sealed class GetBirthdayNoticesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IBirthdayNoticeSettingRepository settings,
    BirthdayNoticesComposer composer)
    : IQueryHandler<GetBirthdayNoticesQuery, BirthdayNoticesDto>
{
    public async Task<BirthdayNoticesDto> HandleAsync(GetBirthdayNoticesQuery query, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, HrPermissions.EmployeeRead);

        var setting = await settings.FindAsync(query.TenantId, cancellationToken);
        return await composer.ComposeAsync(
            query.TenantId, setting, executionContext.HasPermission(HrPermissions.SettingsManage), cancellationToken);
    }
}
