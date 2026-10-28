using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Integrations.Application;
using Modules.Integrations.Application.Shopify;

namespace Modules.Integrations.Api;

public static class IntegrationsEndpoints
{
    /// <summary>Spec 2026-10-15 §7: el inicio B, anónimo. Exento de CSRF por esta ruta exacta (lo abre Shopify, no hay
    /// sesión y se autentica con su HMAC): <c>RequireCsrfHeaderMiddleware</c> la repite literal y la compara ordinal, así que
    /// cambiarla acá exige cambiarla allá.</summary>
    public const string ShopifyInstallStartRoute = "/api/v1/integrations/shopify/install/start";

    /// <summary>Spec 2026-10-15 §7: el canje, anónimo. <b>No</b> está exento de CSRF: lo llama el SPA con fetch (Shopify sólo
    /// redirige el navegador a la página <c>/shopify/callback</c> del SPA), y el SPA manda <c>X-Qep-Client</c> siempre.</summary>
    public const string ShopifyInstallCompleteRoute = "/api/v1/integrations/shopify/install/complete";

    public static IEndpointRouteBuilder MapIntegrationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como pos y companies. Cada endpoint declara su propio
        // RequireAuthorization (spec 2026-10-08, «Endpoints»): el grupo no lleva política. Los
        // handlers revalidan tenant y permiso (doble capa) y responden 403, nunca 404, ante otro tenant.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/integrations")
            .WithTags("Integrations");

        group.MapGet("/catalog", GetCatalogAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionRead)
            .Produces<IntegrationsCatalogResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/connections", ListAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionRead)
            .Produces<ConnectionsResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/connections", CreateAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Accepts<CreateConnectionRequest>("application/json")
            .Produces<ConnectionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/connections/{connectionId:guid}", GetAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionRead)
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/connections/{connectionId:guid}", UpdateAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Accepts<UpdateConnectionRequest>("application/json")
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // P27: sin If-Match, como dice el spec; sólo anota el resultado de la prueba.
        group.MapPost("/connections/{connectionId:guid}/test", TestAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/connections/{connectionId:guid}/pause", PauseAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/connections/{connectionId:guid}/resume", ResumeAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Produces<ConnectionResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/connections/{connectionId:guid}", DeleteAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        // Spec 2026-10-09 §5.2: lo manda el frontend al cerrar el popup de Meta; el code vence en 30 s.
        group.MapPost("/whatsapp/embedded-signup", CompleteSignupAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Accepts<EmbeddedSignupRequest>("application/json")
            .Produces<ConnectionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Spec 2026-10-14 §5.1: lo manda el SPA al volver de la ventana de Instagram con el code; vale 1 hora y una vez.
        group.MapPost("/instagram/connect", ConnectInstagramAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Accepts<InstagramConnectRequest>("application/json")
            .Produces<ConnectionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Spec 2026-10-15 §4.3, A1: el inicio desde QEP. Devuelve la URL de autorización de la tienda; el SPA redirige.
        group.MapPost("/shopify/install/start", StartShopifyInstallForTenantAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Accepts<ShopifyInstallStartForTenantRequest>("application/json")
            .Produces<ShopifyAuthorizeResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-10-15 §4.3 paso 6: el enlace de la instalación canjeada a este tenant. 201 con la conexión, como los demás
        // flujos que crean una; también cuando reenlaza la misma tienda, porque la pantalla hace lo mismo con las dos.
        group.MapPost("/shopify/link", LinkShopifyStoreAsync)
            .RequireAuthorization(IntegrationsPermissions.ConnectionManage)
            .Accepts<ShopifyLinkRequest>("application/json")
            .Produces<ConnectionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    /// <summary>
    /// Spec 2026-10-15 §4.3 y §7: las rutas anónimas de la instalación, fuera de <c>/api/v1/tenants</c> porque todavía no
    /// hay tenant (ni <c>TenantModuleGuard</c>), con el limitador por IP que reciba (<c>public</c>). Hoy, el inicio B.
    /// </summary>
    public static IEndpointRouteBuilder MapShopifyInstallEndpoints(this IEndpointRouteBuilder endpoints, string rateLimiterPolicy)
    {
        endpoints.MapPost(ShopifyInstallStartRoute, StartShopifyInstallAsync)
            .AllowAnonymous()
            .RequireRateLimiting(rateLimiterPolicy)
            .WithTags("Integrations")
            .Accepts<ShopifyInstallStartRequest>("application/json")
            .Produces<ShopifyAuthorizeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        endpoints.MapPost(ShopifyInstallCompleteRoute, CompleteShopifyInstallAsync)
            .AllowAnonymous()
            .RequireRateLimiting(rateLimiterPolicy)
            .WithTags("Integrations")
            .Accepts<ShopifyInstallCompleteRequest>("application/json")
            .Produces<ShopifyInstallCompletedResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // §7: sólo autenticación. El tenant todavía no está elegido: la respuesta lista los del usuario.
        endpoints.MapGet("/api/v1/integrations/shopify/installs/{installId:guid}", GetShopifyInstallAsync)
            .RequireAuthorization()
            .WithTags("Integrations")
            .Produces<ShopifyInstallResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> CompleteShopifyInstallAsync(
        ShopifyInstallCompleteRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new CompleteShopifyInstallCommand(
                request.Code, request.Shop, request.State, request.Hmac, request.Timestamp, request.RedirectUri, request.Host),
            cancellationToken));

    private static async Task<IResult> GetShopifyInstallAsync(
        Guid installId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetShopifyInstallQuery(installId), cancellationToken));

    private static async Task<IResult> StartShopifyInstallForTenantAsync(
        Guid tenantId, ShopifyInstallStartForTenantRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new StartShopifyInstallForTenantCommand(tenantId, request.Shop, request.RedirectUri), cancellationToken));

    private static async Task<IResult> StartShopifyInstallAsync(
        ShopifyInstallStartRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new StartShopifyInstallCommand(request.Shop, request.Hmac, request.Timestamp, request.Host, request.RedirectUri),
            cancellationToken));

    private static async Task<IResult> LinkShopifyStoreAsync(
        Guid tenantId, ShopifyLinkRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var connection = await dispatcher.SendAsync(
            new LinkShopifyStoreCommand(tenantId, request.InstallId ?? Guid.Empty, request.Name), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/integrations/connections/{connection.Id}", connection);
    }

    private static async Task<IResult> ConnectInstagramAsync(
        Guid tenantId, InstagramConnectRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var connection = await dispatcher.SendAsync(
            new ConnectInstagramCommand(tenantId, request.Name, request.Code, request.RedirectUri), cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/integrations/connections/{connection.Id}", connection);
    }

    private static async Task<IResult> GetCatalogAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetIntegrationsCatalogQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListConnectionsQuery(tenantId), cancellationToken));

    private static async Task<IResult> CreateAsync(
        Guid tenantId, CreateConnectionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var connection = await dispatcher.SendAsync(
            new CreateConnectionCommand(tenantId, request.ProviderKey, request.Name, request.Fields, request.Secrets),
            cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/integrations/connections/{connection.Id}", connection);
    }

    private static async Task<IResult> CompleteSignupAsync(
        Guid tenantId, EmbeddedSignupRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var connection = await dispatcher.SendAsync(
            new CompleteWhatsAppSignupCommand(
                tenantId, request.Name, request.Path, request.Event, request.Code, request.WabaId, request.PhoneNumberId, request.BusinessId,
                request.Pin),
            cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/integrations/connections/{connection.Id}", connection);
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetConnectionQuery(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> UpdateAsync(
        Guid tenantId,
        Guid connectionId,
        UpdateConnectionRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateConnectionCommand(
                tenantId, connectionId, RequireVersion(httpContext), request.Name, request.Fields, request.Secrets),
            cancellationToken));

    private static async Task<IResult> TestAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new TestConnectionCommand(tenantId, connectionId), cancellationToken));

    private static async Task<IResult> PauseAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new PauseConnectionCommand(tenantId, connectionId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ResumeAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new ResumeConnectionCommand(tenantId, connectionId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid connectionId, IRequestDispatcher dispatcher, HttpContext httpContext, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(
            new DeleteConnectionCommand(tenantId, connectionId, RequireVersion(httpContext)), cancellationToken);
        return Results.NoContent();
    }

    // Mismo contrato que /pos y /orders-export-layout: sin If-Match 428, vieja 412 (en el handler).
    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded connection version is required.");

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

/// <summary>El cuerpo de <c>POST /connections</c>. <see cref="ToString"/> sólo muestra claves.</summary>
public sealed record CreateConnectionRequest(
    string? ProviderKey,
    string? Name,
    Dictionary<string, string?>? Fields,
    Dictionary<string, string?>? Secrets)
{
    public override string ToString() =>
        $"CreateConnectionRequest {{ ProviderKey = {ProviderKey}, Name = {Name}, "
        + $"Fields = [{(Fields is null ? string.Empty : string.Join(", ", Fields.Keys))}], "
        + $"Secrets = [{(Secrets is null ? string.Empty : string.Join(", ", Secrets.Keys))}] }}";
}

/// <summary>El cuerpo de <c>PUT /connections/{id}</c>: un secreto ausente conserva el guardado (D5).</summary>
public sealed record UpdateConnectionRequest(
    string? Name,
    Dictionary<string, string?>? Fields,
    Dictionary<string, string?>? Secrets)
{
    public override string ToString() =>
        $"UpdateConnectionRequest {{ Name = {Name}, "
        + $"Fields = [{(Fields is null ? string.Empty : string.Join(", ", Fields.Keys))}], "
        + $"Secrets = [{(Secrets is null ? string.Empty : string.Join(", ", Secrets.Keys))}] }}";
}

/// <summary>El cuerpo de <c>POST /integrations/whatsapp/embedded-signup</c> (spec 2026-10-09 §5.2).
/// <see cref="ToString"/> no imprime el <c>code</c> —vence en 30 s, pero se canjea por el token— ni el
/// <c>pin</c>, que es un secreto del número.</summary>
public sealed record EmbeddedSignupRequest(
    string? Name, string? Path, string? Event, string? Code, string? WabaId, string? PhoneNumberId, string? BusinessId, string? Pin = null)
{
    public override string ToString() =>
        $"EmbeddedSignupRequest {{ Name = {Name}, Path = {Path}, Event = {Event}, WabaId = {WabaId}, "
        + $"PhoneNumberId = {PhoneNumberId}, BusinessId = {BusinessId} }}";
}

/// <summary>Spec 2026-10-14 §5.1. <see cref="ToString"/> no imprime el <c>code</c>.</summary>
public sealed record InstagramConnectRequest(string? Name, string? Code, string? RedirectUri)
{
    public override string ToString() => $"InstagramConnectRequest {{ Name = {Name}, RedirectUri = {RedirectUri} }}";
}

/// <summary>El cuerpo del inicio A (spec 2026-10-15 §7): el dominio que escribió la persona y el retorno del SPA.</summary>
public sealed record ShopifyInstallStartForTenantRequest(string? Shop, string? RedirectUri);

/// <summary>El cuerpo del inicio B (spec 2026-10-15 §7): lo que Shopify puso en la App URL, tal cual, más el retorno del
/// SPA. Nada de esto es secreto: el HMAC prueba que lo firmó Shopify.</summary>
public sealed record ShopifyInstallStartRequest(string? Shop, string? Hmac, string? Timestamp, string? Host, string? RedirectUri);

/// <summary>El cuerpo del canje (spec 2026-10-15 §7): lo que Shopify puso en el callback, tal cual, más el retorno con el
/// que se pidió la autorización. <see cref="Host"/> no está en el §7: va si Shopify lo puso, porque entra en la firma.</summary>
public sealed record ShopifyInstallCompleteRequest(
    string? Code, string? Shop, string? State, string? Hmac, string? Timestamp, string? RedirectUri, string? Host = null)
{
    public override string ToString() => $"ShopifyInstallCompleteRequest {{ Shop = {Shop}, RedirectUri = {RedirectUri} }}";
}

/// <summary>El cuerpo del enlace (spec 2026-10-15 §7): la instalación que devolvió el canje y, si la persona lo cambió, el
/// nombre de la conexión. <see cref="InstallId"/> es nullable para que su ausencia sea un 422 en <c>installId</c> y no un
/// 400 del binder.</summary>
public sealed record ShopifyLinkRequest(Guid? InstallId, string? Name);
