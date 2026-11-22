using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Scheduling.Application;

namespace Modules.Scheduling.Api;

public static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como hr y loans: la política frena a quien no tiene el permiso y el handler
        // (SchedulingAuthorization) a quien lo tiene en otro tenant. Las demás rutas llegan con las Tareas 8 a 10.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/scheduling")
            .WithTags("Scheduling");

        MapSetup(group);
        MapLocations(group);
        MapResources(group);
        MapServices(group);
        MapSettings(group);

        return endpoints;
    }

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
