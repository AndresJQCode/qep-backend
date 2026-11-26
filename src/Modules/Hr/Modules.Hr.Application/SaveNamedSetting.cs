using BuildingBlocks.Application;
using FluentValidation;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application;

public enum NamedSettingKind
{
    Area,
    Position,
}

/// <summary>Responde <see cref="AreaDto"/> o <see cref="PositionDto"/> según <see cref="NamedSettingKind"/>.</summary>
public sealed record CreateNamedSettingCommand(Guid TenantId, NamedSettingKind Kind, NamedSettingRequest Request) : ICommand<object>;

/// <param name="IfMatch">La versión que la pantalla cargó; llega por <c>If-Match</c>, crudo: el caso de uso lo lee ya autorizado.</param>
public sealed record UpdateNamedSettingCommand(
    Guid TenantId, NamedSettingKind Kind, Guid Id, NamedSettingRequest Request, string? IfMatch) : ICommand<object>;

public sealed class CreateNamedSettingHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IHrSettingsRepository settings,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    HrSettingsComposer composer,
    IValidator<NamedSettingRequest> validator)
    : ICommandHandler<CreateNamedSettingCommand, object>
{
    public async Task<object> HandleAsync(CreateNamedSettingCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var name = command.Request.Name!;
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        OrganizationSetting created;
        Guid id;
        string resource;
        if (command.Kind == NamedSettingKind.Area)
        {
            var area = Area.Create(AreaId.New(), command.TenantId, name, now);
            settings.Add(area);
            (created, id, resource) = (area, area.Id.Value, HrAuditActions.Resources.Area);
        }
        else
        {
            var position = Position.Create(PositionId.New(), command.TenantId, name, now);
            settings.Add(position);
            (created, id, resource) = (position, position.Id.Value, HrAuditActions.Resources.Position);
        }

        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.SettingsCreated, resource, id, ["name"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(command.TenantId, created, cancellationToken);
    }
}

public sealed class UpdateNamedSettingHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IHrSettingsRepository settings,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    HrSettingsComposer composer,
    IValidator<NamedSettingRequest> validator)
    : ICommandHandler<UpdateNamedSettingCommand, object>
{
    public async Task<object> HandleAsync(UpdateNamedSettingCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);
        await validator.ValidateAndThrowAsync(command.Request, cancellationToken);

        var name = command.Request.Name!;
        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        var changed = new List<string>(1);
        OrganizationSetting updated;
        string resource;
        if (command.Kind == NamedSettingKind.Area)
        {
            var area = await settings.FindAreaAsync(command.TenantId, new AreaId(command.Id), cancellationToken)
                ?? throw HrNotFound.Area(command.Id);
            HrSettingsSupport.EnsureVersion(area.Version, expectedVersion);
            HrSettingsSupport.Diff(changed, "name", area.Name, name.Trim());
            area.Rename(name, now);
            (updated, resource) = (area, HrAuditActions.Resources.Area);
        }
        else
        {
            var position = await settings.FindPositionAsync(command.TenantId, new PositionId(command.Id), cancellationToken)
                ?? throw HrNotFound.Position(command.Id);
            HrSettingsSupport.EnsureVersion(position.Version, expectedVersion);
            HrSettingsSupport.Diff(changed, "name", position.Name, name.Trim());
            position.Rename(name, now);
            (updated, resource) = (position, HrAuditActions.Resources.Position);
        }

        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.SettingsUpdated, resource, command.Id, changed, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.ComposeAsync(command.TenantId, updated, cancellationToken);
    }
}
