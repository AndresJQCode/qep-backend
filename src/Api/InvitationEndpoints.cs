using System.Security.Claims;
using Bootstrapper.Authentication;
using Modules.Identity.Application;
using Modules.Tenancy.Application;

namespace Api;

/// <summary>
/// Endpoints de la raíz de composición para el flujo de invitación por token. El GET es
/// anónimo: quien abre el link del email todavía no tiene sesión, y necesita saber a qué
/// tenant lo invitaron y con qué cuenta entrar antes de autenticarse. El accept exige la
/// sesión QEP normal. La composición entre módulos —la invitación es de Tenancy, el email
/// del invitado es de Identity— vive acá, igual que en AuthSessionEndpoints. El
/// auto-accept del login (/auth/session) sigue intacto; este camino se le suma.
/// </summary>
public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        // Superficie pública de lectura: limitada por IP, como pide el comentario del
        // rate limiter en Program.cs para todo endpoint público que se agregue.
        endpoints.MapGet("/api/v1/invitations/{token}", GetAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiterPolicies.Public)
            .WithTags("Invitations")
            .Produces<InvitationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        endpoints.MapPost("/api/v1/invitations/{token}/accept", AcceptAsync)
            .RequireAuthorization()
            .WithTags("Invitations")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string token,
        IInvitationService invitationService,
        IUserDirectory userDirectory,
        CancellationToken cancellationToken)
    {
        var invitation = await invitationService.FindByTokenAsync(token, cancellationToken);
        var email = await userDirectory.GetEmailAsync(invitation.UserId, cancellationToken);
        return Results.Ok(new InvitationResponse(
            invitation.TenantId.Value,
            invitation.TenantName,
            email ?? string.Empty,
            ToStatus(invitation.Status)));
    }

    private static async Task<IResult> AcceptAsync(
        string token,
        HttpContext httpContext,
        IInvitationService invitationService,
        CancellationToken cancellationToken)
    {
        var userId = RequireUserId(httpContext);
        if (userId is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized);
        }

        await invitationService.AcceptAsync(
            token,
            userId.Value,
            httpContext.TraceIdentifier,
            cancellationToken);
        return Results.NoContent();
    }

    // El estado que ve quien abre el link es el derivado (vencimiento perezoso incluido),
    // pero con el vocabulario documentado del contrato de invitaciones: la SPA renderiza
    // pending/accepted/expired y trata cualquier otro valor como "no aceptable". Por eso
    // Active acá se dice "accepted" — "active" es el vocabulario del filtro del roster
    // (MembershipViewStates.Parse), no el de este link.
    private static string ToStatus(MembershipViewState status) =>
        status switch
        {
            MembershipViewState.Active => "accepted",
            _ => status.ToString().ToLowerInvariant(),
        };

    // Mismo criterio que HttpExecutionContext: primero el id interno QEP (qep_sub, que
    // pone la cookie de sesión) y "sub" como respaldo porque el stub de desarrollo pone el
    // subject QEP directo ahí. Con un bearer real "sub" no es un Guid y no pasa.
    private static Guid? RequireUserId(HttpContext httpContext)
    {
        var value = httpContext.User.FindFirstValue(QepClaimTypes.QepSubject)
            ?? httpContext.User.FindFirstValue(QepClaimTypes.SubjectId);
        return Guid.TryParse(value, out var userId) ? userId : null;
    }
}

public sealed record InvitationResponse(
    Guid TenantId,
    string TenantName,
    string Email,
    string Status);
