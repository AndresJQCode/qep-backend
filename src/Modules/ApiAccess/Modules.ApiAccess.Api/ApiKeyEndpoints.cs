using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.ApiAccess.Application;

namespace Modules.ApiAccess.Api;

/// <summary>El cuerpo de crear una clave. Cada campo admite nulo para que el validador responda con el mapa <c>errors</c>.</summary>
public sealed record CreateApiKeyRequest(
    string? Name,
    IReadOnlyList<string>? Scopes,
    IReadOnlyList<Guid>? ConnectionIds,
    DateTimeOffset? ExpiresAt);

public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Tenant en la ruta, como suppliers: la política frena a quien no tiene el permiso y el handler
        // (ApiAccessAuthorization) a quien lo tiene en otro tenant o sin el módulo developer.
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/api-keys")
            .WithTags("ApiKeys");

        group.MapGet("/", ListApiKeysAsync)
            .RequireAuthorization(ApiAccessPermissions.ApiKeyManage)
            .Produces<IReadOnlyList<ApiKeyDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Antes que /{apiKeyId:guid}/revoke: no chocan, pero así se lee en el orden en que la pantalla las usa.
        group.MapGet("/scopes", ListApiScopesAsync)
            .RequireAuthorization(ApiAccessPermissions.ApiKeyManage)
            .Produces<ApiScopeCatalogDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // La única respuesta que trae la clave completa.
        group.MapPost("/", CreateApiKeyAsync)
            .RequireAuthorization(ApiAccessPermissions.ApiKeyManage)
            .Accepts<CreateApiKeyRequest>("application/json")
            .Produces<CreatedApiKeyDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // POST y no DELETE: la clave no desaparece, queda revocada y a la vista. Revocar dos veces no falla.
        group.MapPost("/{apiKeyId:guid}/revoke", RevokeApiKeyAsync)
            .RequireAuthorization(ApiAccessPermissions.ApiKeyManage)
            .Produces<ApiKeyDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListApiKeysAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListApiKeysQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListApiScopesAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListApiScopesQuery(tenantId), cancellationToken));

    private static async Task<IResult> CreateApiKeyAsync(
        Guid tenantId,
        CreateApiKeyRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var created = await dispatcher.SendAsync(
            new CreateApiKeyCommand(
                tenantId, request.Name, request.Scopes, request.ConnectionIds, request.ExpiresAt),
            cancellationToken);

        return Results.Created($"/api/v1/tenants/{tenantId}/api-keys", created);
    }

    private static async Task<IResult> RevokeApiKeyAsync(
        Guid tenantId,
        Guid apiKeyId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new RevokeApiKeyCommand(tenantId, apiKeyId), cancellationToken));
}
