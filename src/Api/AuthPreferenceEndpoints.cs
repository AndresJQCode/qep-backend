using System.Security.Claims;
using Bootstrapper.Authentication;
using Modules.Identity.Application;

namespace Api;

/// <summary>
/// ACC-03. Preferencia de apariencia de la persona autenticada dentro de su tenant activo.
///
/// <para><b>El tenant no viaja en la ruta, y es deliberado.</b> Sale del claim
/// <c>tenant_id</c>, que <see cref="ExternalClaimsTransformation"/> sólo emite después de
/// resolver <c>X-Tenant-Id</c> y comprobar que hay una membresía activa —su
/// <c>ResolvePermissionsAsync</c> devuelve <c>null</c> cuando no la hay—. Dos cosas salen de
/// ahí: la verificación de aislamiento ya está construida y no se duplica, y una ruta sin
/// <c>tenantId</c> no puede desalinearse del tenant autenticado, que es el defecto que este
/// repositorio ya corrigió una vez (<c>fix(CLI-01)</c>).</para>
///
/// <para>No declara permiso del catálogo de Authorization: una persona edita lo suyo, y eso
/// lo resuelve la identidad de la sesión más la membresía. No audita ni emite eventos —una
/// preferencia visual no es una operación sensible y no le interesa a ningún otro módulo—.</para>
/// </summary>
public static class AuthPreferenceEndpoints
{
    public static IEndpointRouteBuilder MapAuthPreferenceEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/auth/preferences")
            .RequireAuthorization()
            .WithTags("Authentication");

        group.MapGet("/", GetAsync)
            .Produces<UserPreferenceResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", SaveAsync)
            .Accepts<UserPreferenceRequest>("application/json")
            .Produces<UserPreferenceResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        IUserPreferenceService preferences,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(httpContext, out var userId, out var tenantId))
        {
            return ForbidTenant();
        }

        var preference = await preferences.GetAsync(userId, tenantId, cancellationToken);
        return Results.Ok(
            new UserPreferenceResponse(preference.ColorScheme, preference.Mode));
    }

    private static async Task<IResult> SaveAsync(
        UserPreferenceRequest request,
        HttpContext httpContext,
        IUserPreferenceService preferences,
        CancellationToken cancellationToken)
    {
        if (!TryResolveScope(httpContext, out var userId, out var tenantId))
        {
            return ForbidTenant();
        }

        // Los valores inválidos salen como IdentityDomainException desde el dominio y el
        // mapeo central de ApiExceptionHandler los convierte en 422 con su código.
        var saved = await preferences.SaveAsync(
            userId,
            tenantId,
            request.ColorScheme ?? string.Empty,
            request.Mode ?? string.Empty,
            cancellationToken);

        return Results.Ok(new UserPreferenceResponse(saved.ColorScheme, saved.Mode));
    }

    private static bool TryResolveScope(
        HttpContext httpContext,
        out Guid userId,
        out Guid tenantId)
    {
        userId = Guid.Empty;
        tenantId = Guid.Empty;

        var rawUserId = httpContext.User.FindFirstValue(QepClaimTypes.QepSubject);
        var rawTenantId = httpContext.User.FindFirstValue(QepClaimTypes.TenantId);

        return Guid.TryParse(rawUserId, out userId)
            && Guid.TryParse(rawTenantId, out tenantId);
    }

    /// <summary>
    /// `403` genérico: no distingue "no mandaste <c>X-Tenant-Id</c>" de "no sos miembro de
    /// ese tenant". Mismo criterio que el `403` de login, que nunca revela qué existe.
    /// </summary>
    private static IResult ForbidTenant() =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "No active tenant for this session.");
}

public sealed record UserPreferenceRequest(string? ColorScheme, string? Mode);

public sealed record UserPreferenceResponse(string ColorScheme, string Mode);
