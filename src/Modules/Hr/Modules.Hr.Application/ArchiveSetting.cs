using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public enum SettingKind
{
    Site,
    Workplace,
    Area,
    Position,
}

/// <summary>Archiva (<c>Archive</c>) o desarchiva la configuración y responde el DTO de su tipo.</summary>
public sealed record ArchiveSettingCommand(Guid TenantId, SettingKind Kind, Guid Id, bool Archive) : ICommand<object>;

/// <summary>
/// Archivar una sede con algún centro sin archivar es <c>site_has_active_workplaces</c> (422); desarchivar un centro de una sede
/// archivada es <c>site_archived</c> (422); archivar dos veces o desarchivar uno activo es 409. Desarchivar nunca choca por
/// nombre: la unicidad incluye a los archivados.
/// </summary>
public sealed class ArchiveSettingHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IHrSettingsRepository settings,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    HrSettingsComposer composer)
    : ICommandHandler<ArchiveSettingCommand, object>
{
    public async Task<object> HandleAsync(ArchiveSettingCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);

        var (setting, resource) = await FindAsync(command, cancellationToken);
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;

        if (command is { Archive: true } && setting is Site { Archived: false } site
            && await settings.SiteHasActiveWorkplacesAsync(command.TenantId, site.Id, cancellationToken))
        {
            throw new HrDomainException(
                HrErrorCodes.SiteHasActiveWorkplaces, "A site with active workplaces cannot be archived.");
        }

        if (command is { Archive: false } && setting is Workplace { Archived: true } workplace)
        {
            var parent = await settings.FindSiteAsync(command.TenantId, workplace.SiteId, cancellationToken);
            if (parent is null || parent.Archived)
            {
                throw new HrDomainException(
                    HrErrorCodes.SiteArchived, "A workplace of an archived site cannot be unarchived.");
            }
        }

        HrConflicts.Run(() =>
        {
            if (command.Archive)
            {
                setting.Archive(now);
            }
            else
            {
                setting.Unarchive(now);
            }
        });

        HrSettingsSupport.PublishAudit(
            auditPublisher,
            executionContext,
            command.TenantId,
            command.Archive ? HrAuditActions.SettingsArchived : HrAuditActions.SettingsUnarchived,
            resource,
            command.Id,
            ["archived"],
            now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(command.TenantId, setting, cancellationToken);
    }

    private async Task<(OrganizationSetting Setting, string Resource)> FindAsync(
        ArchiveSettingCommand command, CancellationToken cancellationToken) =>
        command.Kind switch
        {
            SettingKind.Site => (
                await settings.FindSiteAsync(command.TenantId, new SiteId(command.Id), cancellationToken)
                    ?? throw HrNotFound.Site(command.Id),
                HrAuditActions.Resources.Site),
            SettingKind.Workplace => (
                await settings.FindWorkplaceAsync(command.TenantId, new WorkplaceId(command.Id), cancellationToken)
                    ?? throw HrNotFound.Workplace(command.Id),
                HrAuditActions.Resources.Workplace),
            SettingKind.Area => (
                await settings.FindAreaAsync(command.TenantId, new AreaId(command.Id), cancellationToken)
                    ?? throw HrNotFound.Area(command.Id),
                HrAuditActions.Resources.Area),
            _ => (
                await settings.FindPositionAsync(command.TenantId, new PositionId(command.Id), cancellationToken)
                    ?? throw HrNotFound.Position(command.Id),
                HrAuditActions.Resources.Position),
        };
}
