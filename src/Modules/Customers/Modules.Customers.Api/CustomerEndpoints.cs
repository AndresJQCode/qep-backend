using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Customers.Application;

namespace Modules.Customers.Api;

public static class CustomerEndpoints
{
    public static IEndpointRouteBuilder MapCustomerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como catalog, storage, tenancy y companies.
        //
        // `CLI-01` declara estas rutas **sin** el tenant (`/api/v1/customers`), y ahi gana el
        // codigo: ese spec es del 2026-08-06, anterior a que `/api/v1/catalog/*` tuviera que
        // realinearse el 2026-08-15 y a que companies naciera con el tenant en la ruta el
        // 2026-08-19. Los cinco modulos construidos coinciden, y el propio gate `CLI-00` pide
        // "aislamiento de tenant revisado: rutas protegidas y sin fuga de datos" — que es lo que
        // esta forma hace explicito.
        //
        // El consumidor todavia llama `/api/v1/customers`. Alinearlo es trabajo del frontend y no
        // de este slice; hasta que ocurra, el modulo esta desconectado a proposito, no roto.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/customers")
            .WithTags("Customers");

        group.MapGet("/", ListCustomersAsync)
            .RequireAuthorization(CustomersPermissions.CustomerRead)
            .Produces<CustomersResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{customerId:guid}", GetCustomerAsync)
            .RequireAuthorization(CustomersPermissions.CustomerRead)
            .Produces<CustomerResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateCustomerAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<CreateCustomerRequest>("application/json")
            .Produces<CustomerResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{customerId:guid}", UpdateCustomerAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<UpdateCustomerRequest>("application/json")
            .Produces<CustomerResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{customerId:guid}/deactivate", DeactivateCustomerAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Produces<CustomerResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // La vuelta de deactivate. `CLI-01` no lo lista; existe porque sin el un cliente inactivo
        // es terminal — Update abre con EnsureActive y nada devuelve IsActive a true. Es la falta
        // que CAT-07 tuvo que corregir en producto despues. Sin permiso nuevo: reactivar es
        // administrar.
        //
        // No hay DELETE. Un cliente lo referencian cotizaciones y documentos ya emitidos, y
        // borrarlo en duro deja huerfano lo que ya se emitio; catalogo y empresas tampoco borran.
        group.MapPost("/{customerId:guid}/activate", ActivateCustomerAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Produces<CustomerResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // 202 y no 201: acepta el archivo, **no crea clientes**. Ver ImportCustomersResponse.
        group.MapPost("/import", ImportCustomersAsync)
            .RequireAuthorization(CustomersPermissions.CustomerImport)
            .Produces<ImportCustomersResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> ListCustomersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        int page = 1,
        int pageSize = CustomerPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListCustomersQuery(tenantId, search, page, pageSize),
            cancellationToken);

        return Results.Ok(new CustomersResponse(
            result.Items.Select(ToListItem).ToArray(),
            result.Total,
            result.Page,
            result.PageSize));
    }

    private static async Task<IResult> GetCustomerAsync(
        Guid tenantId,
        Guid customerId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var customer = await dispatcher.QueryAsync(
            new GetCustomerQuery(tenantId, customerId),
            cancellationToken);

        return Results.Ok(ToResponse(customer));
    }

    private static async Task<IResult> CreateCustomerAsync(
        Guid tenantId,
        CreateCustomerRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var customer = await dispatcher.SendAsync(
            new CreateCustomerCommand(
                tenantId,
                request.Name,
                request.IdentificationType,
                request.IdentificationNumber,
                request.Phone,
                request.Email,
                request.Address,
                request.Department,
                request.City,
                request.Classification,
                request.PriceListId,
                request.WithRetention),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/customers/{customer.Id}",
            ToResponse(customer));
    }

    private static async Task<IResult> UpdateCustomerAsync(
        Guid tenantId,
        Guid customerId,
        UpdateCustomerRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var customer = await dispatcher.SendAsync(
            new UpdateCustomerCommand(
                tenantId,
                customerId,
                request.Name,
                request.IdentificationType,
                request.IdentificationNumber,
                request.Phone,
                request.Email,
                request.Address,
                request.Department,
                request.City,
                request.Classification,
                request.PriceListId,
                request.WithRetention),
            cancellationToken);

        return Results.Ok(ToResponse(customer));
    }

    private static async Task<IResult> DeactivateCustomerAsync(
        Guid tenantId,
        Guid customerId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var customer = await dispatcher.SendAsync(
            new DeactivateCustomerCommand(tenantId, customerId),
            cancellationToken);

        return Results.Ok(ToResponse(customer));
    }

    private static async Task<IResult> ActivateCustomerAsync(
        Guid tenantId,
        Guid customerId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var customer = await dispatcher.SendAsync(
            new ActivateCustomerCommand(tenantId, customerId),
            cancellationToken);

        return Results.Ok(ToResponse(customer));
    }

    private static async Task<IResult> ImportCustomersAsync(
        Guid tenantId,
        IFormFile file,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        // Solo el nombre y el tamano llegan al handler: el contenido no se lee, porque `CLI-01`
        // deja el procesamiento del Excel fuera de alcance. Pasar el stream daria la impresion de
        // que alguien lo consume.
        var result = await dispatcher.SendAsync(
            new ImportCustomersCommand(tenantId, file.FileName, file.Length),
            cancellationToken);

        return Results.Accepted(value: result);
    }

    private static CustomerResponse ToResponse(CustomerDto customer) => new(
        customer.Id,
        customer.Cuc,
        customer.Name,
        customer.IdentificationType,
        customer.IdentificationNumber,
        customer.Phone,
        customer.Email,
        customer.Address,
        customer.Department,
        customer.City,
        customer.Classification,
        customer.PriceListId,
        customer.WithRetention,
        customer.IsActive,
        customer.CreatedAt,
        customer.UpdatedAt);

    // PriceListName va siempre en null: el modulo `pricing` no existe, asi que no hay de donde
    // resolver el nombre. Ver CustomerListItemResponse.
    private static CustomerListItemResponse ToListItem(CustomerDto customer) => new(
        customer.Id,
        customer.Cuc,
        customer.Name,
        customer.IdentificationNumber,
        customer.Phone,
        customer.City,
        customer.Classification,
        null,
        customer.IsActive);
}
