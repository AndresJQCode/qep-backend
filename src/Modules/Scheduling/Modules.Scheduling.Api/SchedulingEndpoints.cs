using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Modules.Scheduling.Application;

namespace Modules.Scheduling.Api;

public static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como hr y loans: la política frena a quien no tiene el permiso y el handler
        // (SchedulingAuthorization) a quien lo tiene en otro tenant.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/scheduling")
            .WithTags("Scheduling");

        MapSetup(group);
        MapLocations(group);
        MapResources(group);
        MapServices(group);
        MapSettings(group);
        MapBlocks(group);
        MapSessions(group);
        MapClosures(group);
        MapAvailability(group);
        MapCustomers(group);
        MapAppointments(group);
        MapReads(group);

        return endpoints;
    }

    // Lecturas BFF: el calendario y el escritorio son `appointment.read` y vienen ya filtrados por el alcance de quien pide.
    private static void MapReads(RouteGroupBuilder group)
    {
        group.MapGet("/calendar", async (
                Guid tenantId,
                DateOnly? date,
                DateOnly? from,
                DateOnly? to,
                Guid? locationId,
                [FromQuery] Guid[]? resourceIds,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(
                    new ListCalendarQuery(tenantId, new CalendarRequest(date, from, to, locationId, resourceIds)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Produces<CalendarDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/desk", async (Guid tenantId, DateOnly? date, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new GetDeskQuery(tenantId, date), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Produces<DeskDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    // Franjas: toda ruta es `availability.manage` (y el handler exige también el `read`), dentro del alcance de recursos de quien pide.
    private static void MapBlocks(RouteGroupBuilder group)
    {
        group.MapGet("/blocks", async (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new ListBlocksQuery(tenantId), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Produces<IReadOnlyList<BlockDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/blocks", async (Guid tenantId, BlockRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var block = await dispatcher.SendAsync(new CreateBlockCommand(tenantId, request), cancellationToken);
                return Results.Created($"/api/v1/tenants/{tenantId}/scheduling/blocks/{block.Id}", block);
            })
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Accepts<BlockRequest>("application/json")
            .Produces<BlockDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/blocks/{id:guid}", async (
                Guid tenantId, Guid id, BlockRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(
                    new UpdateBlockCommand(tenantId, id, request, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Accepts<BlockRequest>("application/json")
            .Produces<BlockDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/blocks/{id:guid}/archive", async (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(new ArchiveBlockCommand(tenantId, id), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Produces<BlockDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // Publicar o quitar una sesión de una franja `PublishedSessions`: el contrato no lo separa de las franjas, es `availability.manage`.
    private static void MapSessions(RouteGroupBuilder group)
    {
        // Spec 2026-11-23 §1: las sesiones publicadas del rango (días del tenant), dentro del alcance de recursos de quien pide.
        group.MapGet("/sessions", async (
                Guid tenantId,
                DateOnly? from,
                DateOnly? to,
                Guid? resourceId,
                IRequestDispatcher dispatcher,
                CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(
                    new ListSessionsQuery(tenantId, new ListSessionsRequest(from, to, resourceId)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Produces<IReadOnlyList<PublishedSessionDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/sessions", async (Guid tenantId, PublishSessionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var session = await dispatcher.SendAsync(new PublishSessionCommand(tenantId, request), cancellationToken);
                return Results.Created($"/api/v1/tenants/{tenantId}/scheduling/sessions/{session.Id}", session);
            })
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Accepts<PublishSessionRequest>("application/json")
            .Produces<PublishedSessionDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/sessions/{id:guid}", async (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                await dispatcher.SendAsync(new RemovePublishedSessionCommand(tenantId, id), cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static void MapClosures(RouteGroupBuilder group)
    {
        group.MapGet("/closures", async (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new ListClosuresQuery(tenantId), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Produces<IReadOnlyList<ClosureDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/closures", async (Guid tenantId, ClosureRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var closure = await dispatcher.SendAsync(new CreateClosureCommand(tenantId, request), cancellationToken);
                return Results.Created($"/api/v1/tenants/{tenantId}/scheduling/closures/{closure.Id}", closure);
            })
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Accepts<ClosureRequest>("application/json")
            .Produces<ClosureDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/closures/{id:guid}", async (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                await dispatcher.SendAsync(new DeleteClosureCommand(tenantId, id), cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(SchedulingPermissions.AvailabilityManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    // Espacios libres de un servicio: `appointment.read`, dentro del alcance de recursos de quien pregunta.
    private static void MapAvailability(RouteGroupBuilder group) =>
        group.MapGet("/availability", async (
                Guid tenantId, Guid? serviceId, DateOnly? from, DateOnly? to, Guid? resourceId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new ListAvailabilityQuery(tenantId, serviceId, from, to, resourceId), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Produces<AvailabilityDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

    // El cliente de la cita se busca y se crea por la Agenda (plan, corrección 5): los roles de la Agenda no llevan permisos de Clientes.
    private static void MapCustomers(RouteGroupBuilder group)
    {
        group.MapGet("/customers", async (Guid tenantId, string? search, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new SearchAppointmentCustomersQuery(tenantId, search), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentManage)
            .Produces<IReadOnlyList<AppointmentCustomerDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/customers", async (Guid tenantId, NewCustomerRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var customer = await dispatcher.SendAsync(new CreateAppointmentCustomerCommand(tenantId, request), cancellationToken);
                return Results.Created($"/api/v1/tenants/{tenantId}/scheduling/customers/{customer.Id}", customer);
            })
            .RequireAuthorization(SchedulingPermissions.AppointmentManage)
            .Accepts<NewCustomerRequest>("application/json")
            .Produces<AppointmentCustomerDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    // Citas: leer es `appointment.read`; reservar y toda transición es `appointment.manage` (y el handler exige también el `read`). Cada
    // transición exige `If-Match` con la `version` que la pantalla cargó y responde la cita completa, ya con su nueva versión.
    private static void MapAppointments(RouteGroupBuilder group)
    {
        group.MapPost("/appointments", async (Guid tenantId, BookAppointmentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var appointment = await dispatcher.SendAsync(new BookAppointmentCommand(tenantId, request), cancellationToken);
                return Results.Created($"/api/v1/tenants/{tenantId}/scheduling/appointments/{appointment.Id}", appointment);
            })
            .RequireAuthorization(SchedulingPermissions.AppointmentManage)
            .Accepts<BookAppointmentRequest>("application/json")
            .Produces<AppointmentDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/appointments/{id:guid}", async (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new GetAppointmentQuery(tenantId, id), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Produces<AppointmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/appointments/{id:guid}/confirm", async (
                Guid tenantId, Guid id, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(new ConfirmAppointmentCommand(tenantId, id, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentManage)
            .ProducesTransition();

        group.MapPost("/appointments/{id:guid}/cancel", async (
                Guid tenantId, Guid id, CancelAppointmentRequest? request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(new CancelAppointmentCommand(tenantId, id, request, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentManage)
            .Accepts<CancelAppointmentRequest>("application/json")
            .ProducesTransition();

        group.MapPost("/appointments/{id:guid}/attend", async (
                Guid tenantId, Guid id, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(new MarkAttendedCommand(tenantId, id, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentManage)
            .ProducesTransition();

        group.MapPost("/appointments/{id:guid}/no-show", async (
                Guid tenantId, Guid id, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(new MarkNoShowCommand(tenantId, id, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentManage)
            .ProducesTransition();

        group.MapPut("/appointments/{id:guid}/reschedule", async (
                Guid tenantId, Guid id, RescheduleAppointmentRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(new RescheduleAppointmentCommand(tenantId, id, request, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentManage)
            .Accepts<RescheduleAppointmentRequest>("application/json")
            .ProducesTransition();
    }

    // Every transition answers the whole appointment and shares the same problems: 403 outside the scope, 404, 409 when a seat is taken
    // (reschedule), 412/428 for If-Match and 422 for a closed appointment, a start before it begins or one the rules do not offer.
    private static RouteHandlerBuilder ProducesTransition(this RouteHandlerBuilder builder) =>
        builder
            .Produces<AppointmentDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    private static void MapSetup(RouteGroupBuilder group) =>
        group.MapGet("/setup", async (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new GetSetupQuery(tenantId), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Produces<SetupDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

    // Sedes: leer es `appointment.read`; toda escritura es `settings.manage` (y el handler exige también el `read`). El `PUT` exige `If-Match`.
    private static void MapLocations(RouteGroupBuilder group)
    {
        group.MapGet("/locations", async (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new ListLocationsQuery(tenantId), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Produces<IReadOnlyList<LocationDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/locations", async (Guid tenantId, LocationRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var location = await dispatcher.SendAsync(new CreateLocationCommand(tenantId, request), cancellationToken);
                return Results.Created($"/api/v1/tenants/{tenantId}/scheduling/locations/{location.Id}", location);
            })
            .RequireAuthorization(SchedulingPermissions.SettingsManage)
            .Accepts<LocationRequest>("application/json")
            .Produces<LocationDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/locations/{id:guid}", async (
                Guid tenantId, Guid id, LocationRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(
                    new UpdateLocationCommand(tenantId, id, request, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.SettingsManage)
            .Accepts<LocationRequest>("application/json")
            .Produces<LocationDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/locations/{id:guid}/archive", async (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(new ArchiveLocationCommand(tenantId, id), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.SettingsManage)
            .Produces<LocationDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    // Recursos: alta y archivo son de `settings.manage`. El `PUT` entra con `appointment.read` y el handler decide: `settings.manage` edita
    // cualquiera; `availability.manage` sólo el recurso vinculado a su membresía (la política no puede pedir «uno de dos permisos»).
    private static void MapResources(RouteGroupBuilder group)
    {
        group.MapGet("/resources", async (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new ListResourcesQuery(tenantId), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Produces<IReadOnlyList<ResourceDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/resources", async (Guid tenantId, ResourceRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var resource = await dispatcher.SendAsync(new CreateResourceCommand(tenantId, request), cancellationToken);
                return Results.Created($"/api/v1/tenants/{tenantId}/scheduling/resources/{resource.Id}", resource);
            })
            .RequireAuthorization(SchedulingPermissions.SettingsManage)
            .Accepts<ResourceRequest>("application/json")
            .Produces<ResourceDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/resources/{id:guid}", async (
                Guid tenantId, Guid id, ResourceRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(
                    new UpdateResourceCommand(tenantId, id, request, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Accepts<ResourceRequest>("application/json")
            .Produces<ResourceDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/resources/{id:guid}/archive", async (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(new ArchiveResourceCommand(tenantId, id), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.SettingsManage)
            .Produces<ResourceDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static void MapServices(RouteGroupBuilder group)
    {
        group.MapGet("/services", async (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new ListServicesQuery(tenantId), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Produces<IReadOnlyList<ServiceDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/services", async (Guid tenantId, ServiceRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
            {
                var service = await dispatcher.SendAsync(new CreateServiceCommand(tenantId, request), cancellationToken);
                return Results.Created($"/api/v1/tenants/{tenantId}/scheduling/services/{service.Id}", service);
            })
            .RequireAuthorization(SchedulingPermissions.SettingsManage)
            .Accepts<ServiceRequest>("application/json")
            .Produces<ServiceDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/services/{id:guid}", async (
                Guid tenantId, Guid id, ServiceRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(
                    new UpdateServiceCommand(tenantId, id, request, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.SettingsManage)
            .Accepts<ServiceRequest>("application/json")
            .Produces<ServiceDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/services/{id:guid}/archive", async (Guid tenantId, Guid id, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(new ArchiveServiceCommand(tenantId, id), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.SettingsManage)
            .Produces<ServiceDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/settings", async (Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.QueryAsync(new GetSettingsQuery(tenantId), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.AppointmentRead)
            .Produces<SettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/settings", async (
                Guid tenantId, SettingsRequest request, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                Results.Ok(await dispatcher.SendAsync(
                    new UpdateSettingsCommand(tenantId, request, IfMatchOf(httpContext)), cancellationToken)))
            .RequireAuthorization(SchedulingPermissions.SettingsManage)
            .Accepts<SettingsRequest>("application/json")
            .Produces<SettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    // El If-Match llega crudo al caso de uso, que lo exige ya autorizado: parsearlo acá daría 428 a quien opera en otro tenant.
    private static string? IfMatchOf(HttpContext httpContext) => httpContext.Request.Headers.IfMatch.ToString();
}
