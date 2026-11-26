using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public sealed record DeleteSettingCommand(Guid TenantId, SettingKind Kind, Guid Id) : ICommand<bool>;

/// <summary>
/// Una configuración usada (por un registro de cargo y lugar, o una sede con algún centro) no se elimina: 409
/// <c>hr.settings.in_use</c>. Se archiva. La base lo refuerza con FK restrict, pero el caso de uso lo dice antes con su código.
/// </summary>
public sealed class DeleteSettingHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IHrSettingsRepository settings,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher)
    : ICommandHandler<DeleteSettingCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteSettingCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);

        string resource;
        switch (command.Kind)
        {
            case SettingKind.Site:
                var site = await settings.FindSiteAsync(command.TenantId, new SiteId(command.Id), cancellationToken)
                    ?? throw HrNotFound.Site(command.Id);
                await EnsureNotInUseAsync(command, cancellationToken);
                settings.Remove(site);
                resource = HrAuditActions.Resources.Site;
                break;
            case SettingKind.Workplace:
                var workplace = await settings.FindWorkplaceAsync(command.TenantId, new WorkplaceId(command.Id), cancellationToken)
                    ?? throw HrNotFound.Workplace(command.Id);
                await EnsureNotInUseAsync(command, cancellationToken);
                settings.Remove(workplace);
                resource = HrAuditActions.Resources.Workplace;
                break;
            case SettingKind.Area:
                var area = await settings.FindAreaAsync(command.TenantId, new AreaId(command.Id), cancellationToken)
                    ?? throw HrNotFound.Area(command.Id);
                await EnsureNotInUseAsync(command, cancellationToken);
                settings.Remove(area);
                resource = HrAuditActions.Resources.Area;
                break;
            default:
                var position = await settings.FindPositionAsync(command.TenantId, new PositionId(command.Id), cancellationToken)
                    ?? throw HrNotFound.Position(command.Id);
                await EnsureNotInUseAsync(command, cancellationToken);
                settings.Remove(position);
                resource = HrAuditActions.Resources.Position;
                break;
        }

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.SettingsDeleted, resource, command.Id, [], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }

    private async Task EnsureNotInUseAsync(DeleteSettingCommand command, CancellationToken cancellationToken)
    {
        if (await settings.IsInUseAsync(command.TenantId, command.Id, cancellationToken))
        {
            throw new ResourceConflictException(
                HrErrorCodes.SettingsInUse, "Ya se está usando: no se puede eliminar. Archívalo en su lugar.");
        }
    }
}
