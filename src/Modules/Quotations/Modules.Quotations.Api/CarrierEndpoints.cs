using BuildingBlocks.Application;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Quotations.Application;
using Modules.Tenancy.Application;

namespace Modules.Quotations.Api;

/// <summary>
/// Transportadoras y el IVA del envío (spec 2026-10-17 §4.3). Los permisos de settings, como el
/// resto de la configuración de Quotations (H13), salvo <c>/shipping-options</c>, que lee quien
/// cotiza (D-F3). Cada mutación devuelve lo que la pantalla repinta: la transportadora tocada, o la
/// pantalla entera cuando el cambio la afecta toda (reordenar, la tasa, quién paga el envío).
/// </summary>
public static class CarrierEndpoints
{
    public static IEndpointRouteBuilder MapCarrierEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}");
        var group = tenant.MapGroup("/carriers").WithTags("Tenant settings");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<CarrierSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<CarrierRequest>("application/json")
            .Produces<CarrierResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{carrierId:guid}", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<CarrierRequest>("application/json")
            .Produces<CarrierResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/{carrierId:guid}/activate", ActivateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Produces<CarrierResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{carrierId:guid}/deactivate", DeactivateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Produces<CarrierResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/order", ReorderAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<ReorderCarriersRequest>("application/json")
            .Produces<CarrierSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/freight-tax-rate", SetFreightTaxRateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<SetFreightTaxRateRequest>("application/json")
            .Produces<CarrierSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/freight-charge-modes", SetFreightChargeModesAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<SetFreightChargeModesRequest>("application/json")
            .Produces<CarrierSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        tenant.MapGet("/shipping-options", GetShippingOptionsAsync)
            .WithTags("Quotations")
            .RequireAuthorization(QuotationsPermissions.QuotationRead)
            .Produces<ShippingOptionsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.QueryAsync(new ListCarriersQuery(tenantId), cancellationToken)));

    private static async Task<IResult> CreateAsync(
        Guid tenantId,
        CarrierRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var carrier = await dispatcher.SendAsync(
            new CreateCarrierCommand(tenantId, request.Name, request.ExternalNames, request.SupportsCashOnDelivery),
            cancellationToken);
        httpContext.Response.Headers.ETag = $"\"{carrier.Version}\"";
        return Results.Created($"/api/v1/tenants/{tenantId}/carriers/{carrier.Id}", ToResponse(carrier));
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid carrierId,
        CarrierRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded carrier version is required.");
        }

        var carrier = await dispatcher.SendAsync(
            new UpdateCarrierCommand(
                tenantId, carrierId, request.Name, request.ExternalNames, request.SupportsCashOnDelivery, expectedVersion),
            cancellationToken);
        return CarrierResult(carrier, httpContext);
    }

    private static async Task<IResult> ActivateAsync(
        Guid tenantId, Guid carrierId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        CarrierResult(
            await dispatcher.SendAsync(new SetCarrierActiveCommand(tenantId, carrierId, IsActive: true), cancellationToken),
            httpContext);

    private static async Task<IResult> DeactivateAsync(
        Guid tenantId, Guid carrierId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        CarrierResult(
            await dispatcher.SendAsync(new SetCarrierActiveCommand(tenantId, carrierId, IsActive: false), cancellationToken),
            httpContext);

    private static async Task<IResult> ReorderAsync(
        Guid tenantId, ReorderCarriersRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.SendAsync(
            new ReorderCarriersCommand(tenantId, request.CarrierIds), cancellationToken)));

    private static async Task<IResult> SetFreightTaxRateAsync(
        Guid tenantId, SetFreightTaxRateRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.SendAsync(
            new SetFreightTaxRateCommand(tenantId, request.TaxRateId), cancellationToken)));

    private static async Task<IResult> SetFreightChargeModesAsync(
        Guid tenantId, SetFreightChargeModesRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await dispatcher.SendAsync(
            new SetFreightChargeModesCommand(tenantId, request.EnabledModes, request.SetsDefaultMode, request.DefaultMode),
            cancellationToken)));

    private static async Task<IResult> GetShippingOptionsAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var options = await dispatcher.QueryAsync(new GetShippingOptionsQuery(tenantId), cancellationToken);
        return Results.Ok(new ShippingOptionsResponse(
            [.. options.Carriers.Select(carrier => new ShippingCarrierOptionResponse(
                carrier.Id, carrier.Name, carrier.SupportsCashOnDelivery))],
            ToResponse(options.FreightTaxRate),
            ToResponse(options.FreightChargeModes),
            options.DefaultFreightChargeMode));
    }

    private static IResult CarrierResult(CarrierDto carrier, HttpContext httpContext)
    {
        httpContext.Response.Headers.ETag = $"\"{carrier.Version}\"";
        return Results.Ok(ToResponse(carrier));
    }

    private static CarrierResponse ToResponse(CarrierDto carrier) => new(
        carrier.Id,
        carrier.Name,
        carrier.ExternalNames,
        carrier.SupportsCashOnDelivery,
        carrier.SortOrder,
        carrier.IsActive,
        carrier.Version);

    private static FreightTaxRateResponse? ToResponse(FreightTaxRateDto? taxRate) =>
        taxRate is null ? null : new(taxRate.Id, taxRate.Name, taxRate.Percentage, taxRate.IsActive);

    private static CarrierSettingsResponse ToResponse(CarrierSettingsDto settings) =>
        new(
            ToResponse(settings.FreightTaxRate),
            [.. settings.Carriers.Select(ToResponse)],
            ToResponse(settings.FreightChargeModes),
            settings.DefaultFreightChargeMode);

    private static FreightChargeModeResponse[] ToResponse(IReadOnlyList<FreightChargeModeDto> modes) =>
        [.. modes.Select(mode => new FreightChargeModeResponse(mode.Mode, mode.IsEnabled))];

    // Copia de OrdersExportLayoutEndpoints.TryParseVersion (privado allá): acepta "3", 3 y W/"3".
    private static bool TryParseVersion(string? etag, out long version)
    {
        version = 0;
        if (string.IsNullOrWhiteSpace(etag))
        {
            return false;
        }

        var normalized = etag.Trim();
        if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..].Trim();
        }

        normalized = normalized.Trim('"');
        return long.TryParse(normalized, out version) && version > 0;
    }
}

/// <summary>Crear y editar llevan el mismo cuerpo. Todo nullable: es el validador el que marca el
/// campo que falta. Sin <c>externalNames</c> es una transportadora sin nombres externos.</summary>
public sealed record CarrierRequest(
    string? Name,
    IReadOnlyList<string?>? ExternalNames,
    bool SupportsCashOnDelivery);

/// <summary>Todos los ids del tenant, en el orden nuevo.</summary>
public sealed record ReorderCarriersRequest(IReadOnlyList<Guid>? CarrierIds);

/// <summary><c>null</c> quita la tasa.</summary>
public sealed record SetFreightTaxRateRequest(Guid? TaxRateId);

/// <summary>
/// Las opciones de «¿Quién paga el envío?» que quedan activas, por nombre (<c>Charged</c>,
/// <c>OnDelivery</c>, <c>Free</c>); las que no vienen se apagan. Vacía está permitida. Repetir una no es error.
///
/// <c>defaultMode</c> (§4.4.1) tiene tres estados, y el cuerpo es lo único que los distingue: **omitido**
/// conserva el actual, <c>null</c> lo quita y un nombre lo fija (tiene que estar en <c>enabledModes</c>, o es
/// 422 con <c>errors.DefaultMode</c>). Es así porque la pantalla de los interruptores no conoce el por defecto
/// cuando sólo prende o apaga uno, y no tiene por qué mandarlo para no borrarlo.
///
/// Por eso es una clase y no un record posicional: System.Text.Json sólo llama el <c>init</c> de
/// <see cref="DefaultMode"/> cuando la propiedad viene en el JSON (también con <c>null</c>), y ese setter es
/// el que prende <see cref="SetsDefaultMode"/>. No hay un <c>Optional&lt;T&gt;</c> en el repo, y un campo
/// <c>hasDefaultMode</c> en el contrato le pasaría al cliente un detalle del servidor. No lo "simplifiques" a
/// un record: omitido y <c>null</c> llegarían iguales y cada guardado de los interruptores borraría el por
/// defecto.
/// </summary>
public sealed class SetFreightChargeModesRequest
{
    private readonly string? _defaultMode;

    public IReadOnlyList<string?>? EnabledModes { get; init; }

    public string? DefaultMode
    {
        get => _defaultMode;
        init
        {
            _defaultMode = value;
            SetsDefaultMode = true;
        }
    }

    /// <summary>Si <see cref="DefaultMode"/> vino en el cuerpo. No es parte del contrato.</summary>
    [JsonIgnore]
    public bool SetsDefaultMode { get; private init; }
}

public sealed record CarrierResponse(
    Guid Id,
    string Name,
    IReadOnlyList<string> ExternalNames,
    bool SupportsCashOnDelivery,
    int SortOrder,
    bool IsActive,
    long Version);

public sealed record FreightTaxRateResponse(Guid Id, string Name, int Percentage, bool IsActive);

/// <summary>Una opción de «¿Quién paga el envío?» con su estado en el tenant (spec §4.4).</summary>
public sealed record FreightChargeModeResponse(string Mode, bool IsEnabled);

/// <summary>La pantalla entera: la tasa del envío y todas las transportadoras, activas e inactivas,
/// en orden (spec §4.3). <c>freightTaxRate</c> es <c>null</c> sin tasa asignada. <c>freightChargeModes</c>
/// trae siempre las tres, en orden <c>Charged</c>, <c>OnDelivery</c>, <c>Free</c>, aunque estén apagadas:
/// la pantalla dibuja un interruptor por cada una sin conocer el enum (§4.4, regla BFF de colecciones de
/// tamaño fijo). <c>defaultFreightChargeMode</c> es <c>null</c> o el nombre de una de las activas (§4.4.1).</summary>
public sealed record CarrierSettingsResponse(
    FreightTaxRateResponse? FreightTaxRate,
    IReadOnlyList<CarrierResponse> Carriers,
    IReadOnlyList<FreightChargeModeResponse> FreightChargeModes,
    string? DefaultFreightChargeMode);

/// <summary>Sólo las transportadoras activas, en orden: lo que el cotizador y el pedido ofrecen. Los modos
/// de cobro, en cambio, van los tres con su estado: el formulario muestra los activos más el que el documento
/// ya tenga guardado aunque esté apagado, y para marcarlo como desactivado tiene que recibirlo (§4.4).
/// <c>defaultFreightChargeMode</c> es el que el formulario aplica cuando el documento envía y todavía no tiene
/// quién paga (§4.4.1): lo aplica el formulario, no el backend (D-F19).</summary>
public sealed record ShippingOptionsResponse(
    IReadOnlyList<ShippingCarrierOptionResponse> Carriers,
    FreightTaxRateResponse? FreightTaxRate,
    IReadOnlyList<FreightChargeModeResponse> FreightChargeModes,
    string? DefaultFreightChargeMode);

public sealed record ShippingCarrierOptionResponse(Guid Id, string Name, bool SupportsCashOnDelivery);
