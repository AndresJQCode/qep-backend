using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Customers.Application;

namespace Modules.Customers.Api;

public static class CustomerEndpoints
{
    // El MIME oficial de .xlsx (OOXML SpreadsheetML). ClosedXML solo escribe este formato, nunca
    // el .xls binario viejo, asi que un solo tipo de contenido alcanza.
    private const string ExcelContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

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

        // La libreta de direcciones como sub-recurso: cada operacion tiene su propia regla y
        // dos personas editando el mismo cliente no se pisan la lista entera.
        group.MapPost("/{customerId:guid}/addresses", AddCustomerAddressAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<CustomerAddressRequest>("application/json")
            .Produces<CustomerResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{customerId:guid}/addresses/{addressId:guid}", UpdateCustomerAddressAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Accepts<CustomerAddressRequest>("application/json")
            .Produces<CustomerResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{customerId:guid}/addresses/{addressId:guid}", RemoveCustomerAddressAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
            .Produces<CustomerResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost(
                "/{customerId:guid}/addresses/{addressId:guid}/principal",
                MakeCustomerAddressPrincipalAsync)
            .RequireAuthorization(CustomersPermissions.CustomerManage)
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

        // 202: el archivo se procesa de verdad (Fase 5) y el cuerpo lleva el detalle fila por
        // fila. 422 solo cuando el archivo es estructuralmente invalido (extension, tamano,
        // columnas faltantes, sin filas de datos) — ver ImportCustomersResponse.
        group.MapPost("/import", ImportCustomersAsync)
            .RequireAuthorization(CustomersPermissions.CustomerImport)
            .Produces<ImportCustomersResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        // Fase 6. Mismo permiso que /import: descargar la plantilla es parte del mismo flujo de
        // carga masiva, no un recurso de lectura general del modulo.
        group.MapGet("/import/template", GetCustomerImportTemplateAsync)
            .RequireAuthorization(CustomersPermissions.CustomerImport)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // El modal de errores del frontend reenvia acá las filas que fallaron (tal cual las
        // recibió de /import) para bajarlas en un Excel nuevo, más chico, listo para corregir y
        // reimportar. Mismo permiso: es el mismo flujo de carga masiva.
        group.MapPost("/import/failed-rows", ExportFailedCustomerRowsAsync)
            .RequireAuthorization(CustomersPermissions.CustomerImport)
            .Accepts<ExportFailedCustomerRowsRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // 202 y no 200 con el archivo: a diferencia de /import/template, la respuesta no lleva el
        // Excel. El archivo se sube al almacenamiento de objetos durante el request y el enlace
        // llega por correo, asi que lo que se acepta acá es la solicitud, no la descarga.
        //
        // Mismo permiso que el listado: exportar es leer el mismo padron que la grilla ya muestra.
        group.MapPost("/export", ExportCustomersAsync)
            .RequireAuthorization(CustomersPermissions.CustomerRead)
            .Produces<ExportCustomersResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> ListCustomersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        string? name = null,
        string? identificationNumber = null,
        string? cuc = null,
        Guid[]? departmentIds = null,
        Guid[]? cityIds = null,
        int page = 1,
        int pageSize = CustomerPaging.DefaultPageSize)
    {
        var result = await dispatcher.QueryAsync(
            new ListCustomersQuery(
                tenantId,
                search,
                name,
                identificationNumber,
                cuc,
                departmentIds,
                cityIds,
                page,
                pageSize),
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
                request.CityId,
                request.ClassificationId,
                request.WithRetention,
                request.VatSurplus),
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
                request.CityId,
                request.ClassificationId,
                request.WithRetention,
                request.VatSurplus),
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
        // A diferencia del slice original (`CLI-01`), el contenido si se lee: el handler lo
        // consume por completo antes de que este metodo devuelva el resultado, asi que el stream
        // sigue vivo durante toda la llamada.
        await using var content = file.OpenReadStream();
        var result = await dispatcher.SendAsync(
            new ImportCustomersCommand(tenantId, file.FileName, file.Length, content),
            cancellationToken);

        return Results.Accepted(value: result);
    }

    private static async Task<IResult> GetCustomerImportTemplateAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var template = await dispatcher.QueryAsync(
            new GetCustomerImportTemplateQuery(tenantId), cancellationToken);

        return Results.File(template.Content, ExcelContentType, template.FileName);
    }

    private static async Task<IResult> ExportFailedCustomerRowsAsync(
        Guid tenantId,
        ExportFailedCustomerRowsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var file = await dispatcher.QueryAsync(
            new ExportFailedCustomerRowsQuery(tenantId, request.Rows), cancellationToken);

        return Results.File(file.Content, ExcelContentType, file.FileName);
    }

    // Los filtros son los mismos que el listado y viajan por query string, no en el cuerpo: es un
    // POST porque tiene efecto (sube un archivo, encola un correo), no porque lleve datos.
    private static async Task<IResult> ExportCustomersAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? search = null,
        string? name = null,
        string? identificationNumber = null,
        string? cuc = null)
    {
        var result = await dispatcher.SendAsync(
            new ExportCustomersCommand(tenantId, search, name, identificationNumber, cuc),
            cancellationToken);

        return Results.Accepted(value: new ExportCustomersResponse(
            result.FileName, result.CustomerCount, result.ExpiresAt));
    }

    private static async Task<IResult> AddCustomerAddressAsync(
        Guid tenantId,
        Guid customerId,
        CustomerAddressRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var customer = await dispatcher.SendAsync(
            new AddCustomerAddressCommand(
                tenantId,
                customerId,
                request.Name,
                request.Address,
                request.CityId,
                request.Phone,
                request.IsPrincipal),
            cancellationToken);

        return Results.Ok(ToResponse(customer));
    }

    private static async Task<IResult> UpdateCustomerAddressAsync(
        Guid tenantId,
        Guid customerId,
        Guid addressId,
        CustomerAddressRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var customer = await dispatcher.SendAsync(
            new UpdateCustomerAddressCommand(
                tenantId,
                customerId,
                addressId,
                request.Name,
                request.Address,
                request.CityId,
                request.Phone,
                request.IsPrincipal),
            cancellationToken);

        return Results.Ok(ToResponse(customer));
    }

    private static async Task<IResult> RemoveCustomerAddressAsync(
        Guid tenantId,
        Guid customerId,
        Guid addressId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var customer = await dispatcher.SendAsync(
            new RemoveCustomerAddressCommand(tenantId, customerId, addressId),
            cancellationToken);

        return Results.Ok(ToResponse(customer));
    }

    private static async Task<IResult> MakeCustomerAddressPrincipalAsync(
        Guid tenantId,
        Guid customerId,
        Guid addressId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var customer = await dispatcher.SendAsync(
            new MakeCustomerAddressPrincipalCommand(tenantId, customerId, addressId),
            cancellationToken);

        return Results.Ok(ToResponse(customer));
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
        customer.City,
        customer.Department,
        customer.Classification,
        customer.Addresses,
        customer.WithRetention,
        customer.VatSurplus,
        customer.IsActive,
        customer.CreatedAt,
        customer.UpdatedAt);

    private static CustomerListItemResponse ToListItem(CustomerDto customer) => new(
        customer.Id,
        customer.Cuc,
        customer.Name,
        customer.IdentificationNumber,
        customer.Phone,
        customer.Email,
        customer.City,
        customer.Department,
        customer.Classification,
        customer.IsActive);
}
