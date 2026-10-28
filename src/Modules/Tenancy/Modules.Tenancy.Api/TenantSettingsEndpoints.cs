using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Api;

public static class TenantSettingsEndpoints
{
    public static IEndpointRouteBuilder MapTenantSettingsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/settings")
            .WithTags("Tenant settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/", UpdateAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdateTenantSettingsRequest>("application/json")
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Spec 2026-09-19: el archivo ya sube por el pipeline de Storage (POST /files, PUT
        // prefirmado, complete); este endpoint sólo lo asigna. Mismo permiso y mismo If-Match
        // obligatorio que el PUT de arriba — administrar el tenant es una sola autoridad.
        group.MapPut("/logo", SetLogoAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<SetTenantLogoRequest>("application/json")
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/logo", RemoveLogoAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Produces<TenantSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Spec 2026-10-27: la regla de redondeo de precios por moneda. Sin If-Match: la regla no es parte
        // del agregado Tenant (no sube su versión), y el PUT reemplaza la lista entera.
        group.MapGet("/price-rounding", GetPriceRoundingAsync)
            .RequireAuthorization(TenancyPermissions.SettingsRead)
            .Produces<PriceRoundingSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/price-rounding", UpdatePriceRoundingAsync)
            .RequireAuthorization(TenancyPermissions.SettingsUpdate)
            .Accepts<UpdatePriceRoundingRequest>("application/json")
            .Produces<PriceRoundingSettingsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetPriceRoundingAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var settings = await dispatcher.QueryAsync(
            new GetPriceRoundingQuery(new TenantId(tenantId)),
            cancellationToken);
        return Results.Ok(PriceRoundingSettingsResponse.From(settings));
    }

    private static async Task<IResult> UpdatePriceRoundingAsync(
        Guid tenantId,
        UpdatePriceRoundingRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var settings = await dispatcher.SendAsync(
            new UpdatePriceRoundingCommand(
                new TenantId(tenantId),
                request.Currencies?
                    .Select(currency => new CurrencyPriceRoundingInput(
                        currency.Currency, currency.Decimals, currency.Mode))
                    .ToArray()),
            cancellationToken);
        return Results.Ok(PriceRoundingSettingsResponse.From(settings));
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var settings = await dispatcher.QueryAsync(
            new GetTenantSettingsQuery(new TenantId(tenantId)),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        UpdateTenantSettingsRequest request,
        IRequestDispatcher dispatcher,
        ITenantDirectory tenantDirectory,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = IfMatchHeader.RequireVersion(httpContext);

        // Ventana de despliegue: un frontend anterior a la moneda y al formato de número no manda
        // esos dos campos. Sin ellos se conserva lo guardado, en vez de un 422 que esa versión no
        // sabe mostrar. El validador del comando sigue exigiendo los dos.
        var defaultCurrency = request.DefaultCurrency;
        var numberFormat = request.NumberFormat;
        if (string.IsNullOrWhiteSpace(defaultCurrency) || string.IsNullOrWhiteSpace(numberFormat))
        {
            var current = await tenantDirectory.GetRegionalSettingsAsync(
                new TenantId(tenantId),
                cancellationToken);
            if (string.IsNullOrWhiteSpace(defaultCurrency))
            {
                defaultCurrency = current?.DefaultCurrency ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(numberFormat))
            {
                numberFormat = current?.NumberFormat ?? string.Empty;
            }
        }

        var settings = await dispatcher.SendAsync(
            new UpdateTenantSettingsCommand(
                new TenantId(tenantId),
                request.DisplayName,
                request.DefaultCulture,
                request.TimeZone,
                request.DateFormat,
                defaultCurrency,
                numberFormat,
                request.Country,
                expectedVersion,
                httpContext.TraceIdentifier,
                request.PersonTypeRequirement),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> SetLogoAsync(
        Guid tenantId,
        SetTenantLogoRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = IfMatchHeader.RequireVersion(httpContext);

        var settings = await dispatcher.SendAsync(
            new SetTenantLogoCommand(
                new TenantId(tenantId), request.FileId, expectedVersion, httpContext.TraceIdentifier),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static async Task<IResult> RemoveLogoAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var expectedVersion = IfMatchHeader.RequireVersion(httpContext);

        var settings = await dispatcher.SendAsync(
            new RemoveTenantLogoCommand(new TenantId(tenantId), expectedVersion, httpContext.TraceIdentifier),
            cancellationToken);
        return SettingsResult(settings, httpContext);
    }

    private static IResult SettingsResult(
        TenantSettingsDto settings,
        HttpContext httpContext)
    {
        httpContext.Response.Headers.ETag = $"\"{settings.Version}\"";
        return Results.Ok(new TenantSettingsResponse(
            settings.TenantId.Value,
            settings.DisplayName,
            settings.DefaultCulture,
            settings.TimeZone,
            settings.DateFormat,
            settings.DefaultCurrency,
            settings.NumberFormat,
            settings.Country,
            settings.Version,
            settings.Logo is { } logo ? new TenantLogoResponse(logo.FileId, logo.Url) : null,
            settings.PersonTypeRequirement));
    }
}

public sealed record UpdateTenantSettingsRequest(
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    string? DefaultCurrency,
    string? NumberFormat,
    string? Country,
    string? PersonTypeRequirement = null);

public sealed record SetTenantLogoRequest(Guid FileId);

public sealed record UpdatePriceRoundingRequest(IReadOnlyList<CurrencyPriceRoundingRequest>? Currencies);

public sealed record CurrencyPriceRoundingRequest(string Currency, int Decimals, string Mode);

public sealed record PriceRoundingSettingsResponse(IReadOnlyList<CurrencyPriceRoundingResponse> Currencies)
{
    public static PriceRoundingSettingsResponse From(PriceRoundingSettingsDto settings) =>
        new(settings.Currencies
            .Select(currency => new CurrencyPriceRoundingResponse(
                currency.Currency, currency.Decimals, currency.Mode, currency.MaxDecimals, currency.IsDefault))
            .ToArray());
}

/// <summary>`MaxDecimals` e `IsDefault` viajan resueltos (regla BFF del repo): el select de decimales
/// de cada fila no conoce el catálogo, y «sin configurar» no se deduce de `Decimals` y `Mode`. El porqué
/// completo está en `CurrencyPriceRoundingDto`. Spec 2026-10-27.</summary>
public sealed record CurrencyPriceRoundingResponse(
    string Currency, int Decimals, string Mode, int MaxDecimals, bool IsDefault);

/// <summary>`Url` viaja resuelta (regla BFF del repo): el sidebar necesita un `src` listo, no una
/// clave que armar con una base que el navegador no conoce. Sólo es `null` si el bucket público se
/// desconfiguró después de asignar el logo; `FileId` viaja igual para que la pantalla ofrezca
/// quitar y no subir (spec 2026-09-19, § Contrato).</summary>
public sealed record TenantLogoResponse(Guid FileId, string? Url);

public sealed record TenantSettingsResponse(
    Guid TenantId,
    string DisplayName,
    string DefaultCulture,
    string TimeZone,
    string DateFormat,
    string DefaultCurrency,
    string NumberFormat,
    /// <summary>ISO-3166-1 alpha-2. El frontend tiene la lista de nombres (la misma del alta de
    /// clientes); el backend sólo guarda el código.</summary>
    string Country,
    long Version,
    TenantLogoResponse? Logo,
    /// <summary>`Never` | `OnCustomerSave` | `OnOrderApproval`: lo guardado, no lo efectivo
    /// (`/auth/me` publica el efectivo). Spec 2026-10-26.</summary>
    string PersonTypeRequirement);
