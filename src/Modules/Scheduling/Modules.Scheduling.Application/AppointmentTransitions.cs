using BuildingBlocks.Application;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record ConfirmAppointmentCommand(Guid TenantId, Guid AppointmentId, string? IfMatch) : ICommand<AppointmentDto>;

public sealed record MarkAttendedCommand(Guid TenantId, Guid AppointmentId, string? IfMatch) : ICommand<AppointmentDto>;

public sealed record MarkNoShowCommand(Guid TenantId, Guid AppointmentId, string? IfMatch) : ICommand<AppointmentDto>;

/// <summary>
/// What every write on an existing appointment shares, in one order: authorize (tenant, permissions, module) → require <c>If-Match</c> →
/// load the appointment → scope (403, never 404, once it is known to exist) → version (412). The version is read <b>after</b> authorizing so
/// that whoever operates on another tenant gets a 403 and not a 428 about a header that was never theirs to use.
/// </summary>
public sealed class AppointmentTransitions(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ITenantClock tenantClock,
    ResourceScopeResolver scopeResolver,
    ISchedulingMemberDirectory members,
    IAppointmentRepository appointments,
    ISchedulingAuditPublisher auditPublisher,
    ISchedulingUnitOfWork unitOfWork,
    AppointmentComposer composer)
{
    public async Task<(Appointment Appointment, ResourceScope Scope)> LoadAsync(
        Guid tenantId, Guid appointmentId, string? ifMatch, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, tenantId, cancellationToken,
            SchedulingPermissions.AppointmentRead, SchedulingPermissions.AppointmentManage);
        var expectedVersion = SchedulingIfMatch.Require(ifMatch);

        var appointment = await appointments.FindAsync(tenantId, new AppointmentId(appointmentId), cancellationToken)
            ?? throw SchedulingNotFound.Appointment(appointmentId);
        var scope = await scopeResolver.ResolveAsync(tenantId, cancellationToken);
        if (!scope.Includes(appointment.ResourceId.Value))
        {
            throw SchedulingAuthorization.Denied();
        }

        SchedulingIfMatch.EnsureVersion(appointment.Version, expectedVersion);
        return (appointment, scope);
    }

    /// <summary>Who makes the change: the membership of the caller, or nobody in the history when the client acts through a channel.</summary>
    public async Task<SchedulingActor> ActorAsync(Guid tenantId, bool actsAsClient, CancellationToken cancellationToken)
    {
        var member = actsAsClient ? null : await members.FindBySubjectAsync(tenantId, executionContext.SubjectId, cancellationToken);
        return SchedulingActor.Human(executionContext.SubjectId, member?.MemberId);
    }

    /// <summary>The instant the change happens, by the clock of the tenant.</summary>
    public async Task<DateTimeOffset> NowAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await tenantClock.GetAsync(tenantId, cancellationToken)).UtcNow;

    /// <summary>The whole appointment with its new version, for the handlers whose core already saved the change.</summary>
    public Task<AppointmentDto> ComposeAsync(Guid tenantId, Appointment appointment, CancellationToken cancellationToken) =>
        composer.ComposeAsync(tenantId, appointment, cancellationToken);

    /// <summary>Audit entry and save in the same transaction as the change, then the whole appointment with its new version.</summary>
    public async Task<AppointmentDto> SaveAsync(
        Guid tenantId,
        Appointment appointment,
        SchedulingActor actor,
        string auditAction,
        IReadOnlyCollection<string> changedFields,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        SchedulingAudit.Publish(
            auditPublisher, actor, tenantId, auditAction, SchedulingAuditActions.Resources.Appointment,
            appointment.Id.Value, changedFields, now);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await composer.ComposeAsync(tenantId, appointment, cancellationToken);
    }

    /// <summary>A transition that touches only the appointment (no seat moves): confirm, attended and no-show.</summary>
    public async Task<AppointmentDto> ApplyAsync(
        Guid tenantId,
        Guid appointmentId,
        string? ifMatch,
        string auditAction,
        Action<Appointment, Guid?, DateTimeOffset> change,
        CancellationToken cancellationToken)
    {
        var (appointment, _) = await LoadAsync(tenantId, appointmentId, ifMatch, cancellationToken);
        var actor = await ActorAsync(tenantId, actsAsClient: false, cancellationToken);
        var now = await NowAsync(tenantId, cancellationToken);
        change(appointment, actor.HistoryMemberId, now);
        return await SaveAsync(tenantId, appointment, actor, auditAction, ["status"], now, cancellationToken);
    }
}

public sealed class ConfirmAppointmentHandler(AppointmentTransitions transitions)
    : ICommandHandler<ConfirmAppointmentCommand, AppointmentDto>
{
    public Task<AppointmentDto> HandleAsync(ConfirmAppointmentCommand command, CancellationToken cancellationToken) =>
        transitions.ApplyAsync(
            command.TenantId, command.AppointmentId, command.IfMatch, SchedulingAuditActions.AppointmentConfirmed,
            (appointment, actor, now) => appointment.Confirm(actor, now), cancellationToken);
}

/// <summary>Completed and no-show are marked only once the appointment started (422 <c>before_start</c> otherwise): the domain decides.</summary>
public sealed class MarkAttendedHandler(AppointmentTransitions transitions)
    : ICommandHandler<MarkAttendedCommand, AppointmentDto>
{
    public Task<AppointmentDto> HandleAsync(MarkAttendedCommand command, CancellationToken cancellationToken) =>
        transitions.ApplyAsync(
            command.TenantId, command.AppointmentId, command.IfMatch, SchedulingAuditActions.AppointmentAttended,
            (appointment, actor, now) => appointment.MarkAttended(actor, now), cancellationToken);
}

public sealed class MarkNoShowHandler(AppointmentTransitions transitions)
    : ICommandHandler<MarkNoShowCommand, AppointmentDto>
{
    public Task<AppointmentDto> HandleAsync(MarkNoShowCommand command, CancellationToken cancellationToken) =>
        transitions.ApplyAsync(
            command.TenantId, command.AppointmentId, command.IfMatch, SchedulingAuditActions.AppointmentNoShow,
            (appointment, actor, now) => appointment.MarkNoShow(actor, now), cancellationToken);
}
