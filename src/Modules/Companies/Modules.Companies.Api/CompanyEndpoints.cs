using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Companies.Application;

namespace Modules.Companies.Api;

public static class CompanyEndpoints
{
    public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como catalog, storage y tenancy. El consumidor llamaba a
        // `/api/v1/companies` sin el tenant; esas rutas eran especulativas y nunca existieron —
        // exactamente lo que le paso a `/api/v1/catalog/*` hasta que se realineo el 2026-08-15.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/companies")
            .WithTags("Companies");

        group.MapGet("/", ListCompaniesAsync)
            .RequireAuthorization(CompaniesPermissions.CompanyRead)
            .Produces<CompaniesResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{companyId:guid}", GetCompanyAsync)
            .RequireAuthorization(CompaniesPermissions.CompanyRead)
            .Produces<CompanyResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateCompanyAsync)
            .RequireAuthorization(CompaniesPermissions.CompanyManage)
            .Accepts<CreateCompanyRequest>("application/json")
            .Produces<CompanyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{companyId:guid}", UpdateCompanyAsync)
            .RequireAuthorization(CompaniesPermissions.CompanyManage)
            .Accepts<UpdateCompanyRequest>("application/json")
            .Produces<CompanyResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{companyId:guid}/deactivate", DeactivateCompanyAsync)
            .RequireAuthorization(CompaniesPermissions.CompanyManage)
            .Produces<CompanyResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // La vuelta de deactivate. Verbo dedicado y no un isActive editable en el PUT: un booleano
        // dejaria el cambio de estado sin evento de auditoria propio y sin invariante que lo
        // custodie. Sin permiso nuevo — activar es administrar.
        //
        // No hay DELETE. Una empresa la referencian cotizaciones y documentos ya emitidos, y
        // borrarla en duro deja huerfano lo que ya se emitio; catalog tampoco borra productos.
        // Si el gate del modulo decide lo contrario, el verbo llega con su propio slice.
        group.MapPost("/{companyId:guid}/activate", ActivateCompanyAsync)
            .RequireAuthorization(CompaniesPermissions.CompanyManage)
            .Produces<CompanyResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListCompaniesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        string? status = null)
    {
        var companies = await dispatcher.QueryAsync(
            new ListCompaniesQuery(tenantId, search, CompanyStatusFilterParser.Parse(status)),
            cancellationToken);

        return Results.Ok(new CompaniesResponse(companies.Select(ToListItem).ToArray()));
    }

    private static async Task<IResult> GetCompanyAsync(
        Guid tenantId,
        Guid companyId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var company = await dispatcher.QueryAsync(
            new GetCompanyQuery(tenantId, companyId),
            cancellationToken);

        return Results.Ok(ToResponse(company));
    }

    private static async Task<IResult> CreateCompanyAsync(
        Guid tenantId,
        CreateCompanyRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var company = await dispatcher.SendAsync(
            new CreateCompanyCommand(
                tenantId,
                request.Name,
                request.AccountNumber,
                request.TaxId,
                request.Phone,
                request.Email,
                request.Address),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/companies/{company.Id}",
            ToResponse(company));
    }

    private static async Task<IResult> UpdateCompanyAsync(
        Guid tenantId,
        Guid companyId,
        UpdateCompanyRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var company = await dispatcher.SendAsync(
            new UpdateCompanyCommand(
                tenantId,
                companyId,
                request.Name,
                request.AccountNumber,
                request.TaxId,
                request.Phone,
                request.Email,
                request.Address),
            cancellationToken);

        return Results.Ok(ToResponse(company));
    }

    private static async Task<IResult> DeactivateCompanyAsync(
        Guid tenantId,
        Guid companyId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var company = await dispatcher.SendAsync(
            new DeactivateCompanyCommand(tenantId, companyId),
            cancellationToken);

        return Results.Ok(ToResponse(company));
    }

    private static async Task<IResult> ActivateCompanyAsync(
        Guid tenantId,
        Guid companyId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var company = await dispatcher.SendAsync(
            new ActivateCompanyCommand(tenantId, companyId),
            cancellationToken);

        return Results.Ok(ToResponse(company));
    }

    private static CompanyResponse ToResponse(CompanyDto company) => new(
        company.Id,
        company.Name,
        company.AccountNumber,
        company.TaxId,
        company.IsActive,
        company.Phone,
        company.Email,
        company.Address,
        company.CreatedAt,
        company.UpdatedAt);

    private static CompanyListItemResponse ToListItem(CompanyDto company) => new(
        company.Id,
        company.Name,
        company.AccountNumber,
        company.TaxId,
        company.Phone,
        company.IsActive);
}
