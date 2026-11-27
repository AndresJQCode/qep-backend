using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.BirthdayNotices;

/// <param name="IfMatch">The loaded version, raw from <c>If-Match</c>: read after authorizing. Absent on the very first save.</param>
public sealed record SaveBirthdayNoticesCommand(Guid TenantId, BirthdayNoticeRequest Request, string? IfMatch)
    : ICommand<BirthdayNoticesDto>;

/// <summary>
/// Upsert of the birthday notice setting (spec 2026-11-26 §5.1). With no row the PUT creates it and <c>If-Match</c> must be absent
/// (sending one means the screen believes in a version that does not exist: 412); with a row <c>If-Match</c> is required (428) and
/// must be the current version (412).
/// </summary>
public sealed class SaveBirthdayNoticesHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IBirthdayNoticeSettingRepository settings,
    IHrMemberDirectory members,
    IHrWhatsAppConnections whatsAppConnections,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    BirthdayNoticesComposer composer,
    IValidator<BirthdayNoticeRequest> validator)
    : ICommandHandler<SaveBirthdayNoticesCommand, BirthdayNoticesDto>
{
    private const string ManagersField = "managerMemberIds";
    private const string ConnectionField = "whatsAppConnectionId";

    public async Task<BirthdayNoticesDto> HandleAsync(SaveBirthdayNoticesCommand command, CancellationToken cancellationToken)
    {
        var tenantId = command.TenantId;
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, tenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);

        var setting = await settings.FindAsync(tenantId, cancellationToken);
        if (setting is null)
        {
            if (!string.IsNullOrWhiteSpace(command.IfMatch))
            {
                throw new RequestConcurrencyException("concurrency.conflict", "The setting has not been saved yet: there is no version to match.");
            }
        }
        else
        {
            HrSettingsSupport.EnsureVersion(setting.Version, HrIfMatch.Require(command.IfMatch));
        }

        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        // The author of the WhatsApp messages is whoever last saved: a caller without a membership here cannot own them.
        var author = await members.FindBySubjectAsync(tenantId, executionContext.SubjectId, cancellationToken)
            ?? throw HrAuthorization.Denied();

        var request = command.Request;
        var values = new BirthdayNoticeValues(
            request.ManagerMemberIds!.Distinct().ToList(),
            request.LeadDays ?? BirthdayNoticeSetting.DefaultLeadDays,
            request.WhatsAppEnabled ?? false,
            request.WhatsAppConnectionId,
            request.EmailEnabled ?? false);

        await EnsureManagersAsync(tenantId, values.ManagerMemberIds, cancellationToken);
        if (values.WhatsAppEnabled
            && values.WhatsAppConnectionId is { } connectionId
            && !await whatsAppConnections.IsWhatsAppConnectionAsync(tenantId, connectionId, cancellationToken))
        {
            throw FieldError(
                HrErrorCodes.BirthdayConnectionInvalid, "Esa conexión no es una conexión de WhatsApp de esta empresa.", ConnectionField);
        }

        var now = (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;
        List<string> changed;
        if (setting is null)
        {
            // The domain throws connection_required here when WhatsApp is on without a connection.
            setting = BirthdayNoticeSetting.Create(tenantId, values, author.MemberId, now);
            settings.Add(setting);
            changed = [ManagersField, "leadDays", "whatsAppEnabled", ConnectionField, "emailEnabled"];
        }
        else
        {
            changed = Changed(setting, values);
            setting.Update(values, author.MemberId, now);
        }

        // The setting is one per tenant, so the tenant id is the audited resource id.
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, tenantId, HrAuditActions.BirthdayNoticesSaved,
            HrAuditActions.Resources.BirthdayNotices, tenantId, changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(tenantId, setting, cancellationToken);
    }

    // Every manager must be an active member of the tenant (FindAsync never returns another tenant's) and read employees,
    // because the digest carries employee names.
    private async Task EnsureManagersAsync(Guid tenantId, IReadOnlyList<Guid> managerIds, CancellationToken cancellationToken)
    {
        if (managerIds.Count == 0)
        {
            return;
        }

        var active = (await members.FindAsync(tenantId, managerIds, cancellationToken))
            .Where(member => member.IsActive).Select(member => member.MemberId).ToHashSet();
        if (managerIds.Any(id => !active.Contains(id)))
        {
            throw FieldError(
                HrErrorCodes.BirthdayManagerNotFound, "Una de las personas elegidas no es un miembro activo de la empresa.", ManagersField);
        }

        var withAccess = (await members.ListWithPermissionAsync(tenantId, HrPermissions.EmployeeRead, cancellationToken))
            .Select(member => member.MemberId).ToHashSet();
        if (managerIds.Any(id => !withAccess.Contains(id)))
        {
            throw FieldError(
                HrErrorCodes.BirthdayManagerWithoutAccess,
                "Una de las personas elegidas no tiene acceso a RRHH, así que no puede recibir nombres de empleados.",
                ManagersField);
        }
    }

    private static List<string> Changed(BirthdayNoticeSetting setting, BirthdayNoticeValues values)
    {
        var changed = new List<string>(5);
        if (!setting.ManagerMemberIds.ToHashSet().SetEquals(values.ManagerMemberIds))
        {
            changed.Add(ManagersField);
        }

        HrSettingsSupport.Diff(changed, "leadDays", setting.LeadDays, values.LeadDays);
        HrSettingsSupport.Diff(changed, "whatsAppEnabled", setting.WhatsAppEnabled, values.WhatsAppEnabled);
        HrSettingsSupport.Diff(
            changed, ConnectionField, setting.WhatsAppConnectionId, values.WhatsAppEnabled ? values.WhatsAppConnectionId : null);
        HrSettingsSupport.Diff(changed, "emailEnabled", setting.EmailEnabled, values.EmailEnabled);
        return changed;
    }

    private static HrDomainException FieldError(string code, string message, string field) =>
        new(code, message, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] });
}
