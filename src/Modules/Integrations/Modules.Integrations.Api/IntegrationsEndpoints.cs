using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Integrations.Application;

namespace Modules.Integrations.Api;

public static class IntegrationsEndpoints
{
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

        return endpoints;
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
                tenantId, request.Name, request.Path, request.Event, request.Code, request.WabaId, request.PhoneNumberId, request.BusinessId),
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
/// <see cref="ToString"/> no imprime el <c>code</c>: vence en 30 s, pero se canjea por el token.</summary>
public sealed record EmbeddedSignupRequest(
    string? Name, string? Path, string? Event, string? Code, string? WabaId, string? PhoneNumberId, string? BusinessId)
{
    public override string ToString() =>
        $"EmbeddedSignupRequest {{ Name = {Name}, Path = {Path}, Event = {Event}, WabaId = {WabaId}, "
        + $"PhoneNumberId = {PhoneNumberId}, BusinessId = {BusinessId} }}";
}
