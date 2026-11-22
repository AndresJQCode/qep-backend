using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Payroll.Application;

namespace Modules.Payroll.Api;

public static class PayrollEndpoints
{
    public static IEndpointRouteBuilder MapPayrollEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como hr y loans: la política frena a quien no tiene el permiso y el handler (PayrollAuthorization) a
        // quien lo tiene en otro tenant. La política de cada ruta es el permiso en negrita de la tabla de rutas del contrato.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/payroll")
            .WithTags("Payroll");

        MapSettings(group);

        return endpoints;
    }

    // Empresas, configuración y conceptos. Leer acepta cualquiera de `payroll.period.read` o `payroll.settings.manage`
    // (PayrollPolicies.SettingsView); toda escritura es sólo `payroll.settings.manage`. El `PUT` exige `If-Match`; archivar y
    // desarchivar, no.
    private static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/companies", ListCompaniesAsync)
            .RequireAuthorization(PayrollPolicies.SettingsView)
            .Produces<IReadOnlyList<PayrollCompanyDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/companies/{companyId:guid}/settings", GetSettingsAsync)
            .RequireAuthorization(PayrollPolicies.SettingsView)
            .Produces<PayrollSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/companies/{companyId:guid}/settings", UpdateSettingsAsync)
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Accepts<PayrollSettingsRequest>("application/json")
            .Produces<PayrollSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/companies/{companyId:guid}/concepts", CreateConceptAsync)
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Accepts<ConceptRequest>("application/json")
            .Produces<PayrollConceptDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/companies/{companyId:guid}/concepts/{conceptId:guid}", UpdateConceptAsync)
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Accepts<ConceptRequest>("application/json")
            .Produces<PayrollConceptDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/companies/{companyId:guid}/concepts/{conceptId:guid}/archive", (Guid tenantId, Guid companyId, Guid conceptId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ArchiveConceptAsync(tenantId, companyId, conceptId, archive: true, dispatcher, cancellationToken))
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Produces<PayrollConceptDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/companies/{companyId:guid}/concepts/{conceptId:guid}/unarchive", (Guid tenantId, Guid companyId, Guid conceptId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
                ArchiveConceptAsync(tenantId, companyId, conceptId, archive: false, dispatcher, cancellationToken))
            .RequireAuthorization(PayrollPermissions.SettingsManage)
            .Produces<PayrollConceptDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListCompaniesAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListPayrollCompaniesQuery(tenantId), cancellationToken));

    private static async Task<IResult> GetSettingsAsync(
        Guid tenantId, Guid companyId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetPayrollSettingsQuery(tenantId, companyId), cancellationToken));

    private static async Task<IResult> UpdateSettingsAsync(
        Guid tenantId,
        Guid companyId,
        PayrollSettingsRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdatePayrollSettingsCommand(tenantId, companyId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> CreateConceptAsync(
        Guid tenantId, Guid companyId, ConceptRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var concept = await dispatcher.SendAsync(new CreateConceptCommand(tenantId, companyId, request), cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/payroll/companies/{companyId}/concepts/{concept.Id}", concept);
    }

    private static async Task<IResult> UpdateConceptAsync(
        Guid tenantId,
        Guid companyId,
        Guid conceptId,
        ConceptRequest request,
        HttpContext httpContext,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateConceptCommand(tenantId, companyId, conceptId, request, IfMatchOf(httpContext)), cancellationToken));

    private static async Task<IResult> ArchiveConceptAsync(
        Guid tenantId, Guid companyId, Guid conceptId, bool archive, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ArchiveConceptCommand(tenantId, companyId, conceptId, archive), cancellationToken));

    // El If-Match llega crudo al caso de uso, que lo exige ya autorizado: parsearlo acá daría 428 a quien opera en otro tenant.
    private static string? IfMatchOf(HttpContext httpContext) => httpContext.Request.Headers.IfMatch.ToString();
}
