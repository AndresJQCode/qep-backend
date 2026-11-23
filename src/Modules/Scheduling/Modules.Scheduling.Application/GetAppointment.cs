using BuildingBlocks.Application;
using Modules.Scheduling.Domain;
using Modules.Tenancy.Application;

namespace Modules.Scheduling.Application;

public sealed record GetAppointmentQuery(Guid TenantId, Guid AppointmentId) : IQuery<AppointmentDto>;

/// <summary>
/// La cita con su historial. Una cita del tenant fuera del alcance de quien pregunta responde 403 y una que no existe en el tenant, 404:
/// el alcance se revisa sobre lo que la base devolvió, nunca antes, para no decir si existe a quien no la puede ver.
/// </summary>
public sealed class GetAppointmentHandler(
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    ResourceScopeResolver scopeResolver,
    IAppointmentRepository appointments,
    AppointmentComposer composer)
    : IQueryHandler<GetAppointmentQuery, AppointmentDto>
{
    public async Task<AppointmentDto> HandleAsync(GetAppointmentQuery query, CancellationToken cancellationToken)
    {
        await SchedulingAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, cancellationToken, SchedulingPermissions.AppointmentRead);

        var appointment = await appointments.FindAsync(query.TenantId, new AppointmentId(query.AppointmentId), cancellationToken)
            ?? throw SchedulingNotFound.Appointment(query.AppointmentId);
        if (!(await scopeResolver.ResolveAsync(query.TenantId, cancellationToken)).Includes(appointment.ResourceId.Value))
        {
            throw SchedulingAuthorization.Denied();
        }

        return await composer.ComposeAsync(query.TenantId, appointment, cancellationToken);
    }
}
