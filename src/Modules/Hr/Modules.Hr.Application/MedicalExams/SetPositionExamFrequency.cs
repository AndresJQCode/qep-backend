using BuildingBlocks.Application;
using Modules.Hr.Domain;
using Modules.Tenancy.Application;

namespace Modules.Hr.Application.MedicalExams;

/// <summary>
/// The body of <c>PUT /hr/positions/{id}/exam-frequency</c>. A separate route from <c>PUT /positions/{id}</c> on purpose: an older
/// frontend that only sends the name never erases the frequency. <c>null</c> means the position carries no periodic exam.
/// </summary>
public sealed record ExamFrequencyRequest(int? Months);

/// <param name="IfMatch">The version of the position the screen loaded; it arrives raw and is read once authorized.</param>
public sealed record SetPositionExamFrequencyCommand(Guid TenantId, Guid Id, ExamFrequencyRequest Request, string? IfMatch)
    : ICommand<PositionDto>;

public sealed class SetPositionExamFrequencyHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    IHrSettingsRepository settings,
    IHrUnitOfWork unitOfWork,
    IHrAuditPublisher auditPublisher,
    HrSettingsComposer composer)
    : ICommandHandler<SetPositionExamFrequencyCommand, PositionDto>
{
    public async Task<PositionDto> HandleAsync(SetPositionExamFrequencyCommand command, CancellationToken cancellationToken)
    {
        await HrAuthorization.EnsureAsync(
            executionContext, tenantModules, command.TenantId, cancellationToken, HrPermissions.EmployeeRead, HrPermissions.SettingsManage);
        var expectedVersion = HrIfMatch.Require(command.IfMatch);

        var position = await settings.FindPositionAsync(command.TenantId, new PositionId(command.Id), cancellationToken)
            ?? throw HrNotFound.Position(command.Id);
        HrSettingsSupport.EnsureVersion(position.Version, expectedVersion);

        var now = (await tenantClock.GetAsync(command.TenantId, cancellationToken)).UtcNow;
        position.SetExamFrequency(command.Request.Months, now);

        HrSettingsSupport.PublishAudit(
            auditPublisher, executionContext, command.TenantId, HrAuditActions.SettingsUpdated, HrAuditActions.Resources.Position,
            command.Id, ["examFrequencyMonths"], now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return await composer.PositionAsync(command.TenantId, position, cancellationToken);
    }
}
