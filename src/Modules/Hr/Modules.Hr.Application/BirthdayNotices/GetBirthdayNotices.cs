using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.BirthdayNotices;

public sealed record GetBirthdayNoticesQuery(Guid TenantId) : IQuery<BirthdayNoticesDto>;

/// <summary>Builds the DTO of the setting for both the GET and the PUT: each manager with the flags the screen warns with.</summary>
public sealed class BirthdayNoticesComposer(
    IHrMemberDirectory members, IHrMemberContacts contacts)
{
    public async Task<BirthdayNoticesDto> ComposeAsync(
        Guid tenantId, BirthdayNoticeSetting? setting, CancellationToken cancellationToken)
    {
        if (setting is null)
        {
            return new BirthdayNoticesDto([], BirthdayNoticeSetting.DefaultLeadDays, false, null, false, null);
        }

        var ids = setting.ManagerMemberIds;
        var found = (await members.FindAsync(tenantId, ids, cancellationToken)).ToDictionary(member => member.MemberId);
        var withAccess = ids.Count == 0
            ? new HashSet<Guid>()
            : (await members.ListWithPermissionAsync(tenantId, HrPermissions.EmployeeRead, cancellationToken))
                .Select(member => member.MemberId).ToHashSet();
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
            managers, setting.LeadDays, setting.WhatsAppEnabled, setting.WhatsAppConnectionId, setting.EmailEnabled, setting.Version);
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
        return await composer.ComposeAsync(query.TenantId, setting, cancellationToken);
    }
}
