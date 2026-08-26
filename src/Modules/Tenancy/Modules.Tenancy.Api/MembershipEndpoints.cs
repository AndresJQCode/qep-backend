using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Api;

public static class MembershipEndpoints
{
    public static IEndpointRouteBuilder MapMembershipEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/memberships")
            .WithTags("Memberships");

        group.MapPost("/", InviteAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipInvite)
            .Accepts<MembershipInviteRequest>("application/json")
            .Produces<MembershipResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // `state` acepta el estado que se muestra —pending, expired, active, suspended,
        // removed—, no el que guarda la tabla: "vencida" se deriva del ExpiresAt contra el
        // reloj del servidor. Uno desconocido responde 422 en vez de ignorarse.
        group.MapGet("/", ListAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipRead)
            .Produces<MembershipListResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{membershipId:guid}/suspend", SuspendAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipManage)
            .Produces<MembershipListItemResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{membershipId:guid}/remove", RemoveAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipManage)
            .Produces<MembershipListItemResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Sin If-Match, igual que suspend y remove: el backend no verifica precondición en
        // estas operaciones y agregarla acá inventaría una que nada comprueba. AUTH-11.
        group.MapPost("/{membershipId:guid}/reactivate", ReactivateAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipManage)
            .Produces<MembershipListItemResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPatch("/{membershipId:guid}/roles", UpdateRolesAsync)
            .RequireAuthorization(TenancyPermissions.AdvisorshipManage)
            .Accepts<MembershipRolesUpdateRequest>("application/json")
            .Produces<MembershipListItemResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return endpoints;
    }

    private static async Task<IResult> UpdateRolesAsync(
        Guid tenantId,
        Guid membershipId,
        MembershipRolesUpdateRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseVersion(httpContext.Request.Headers.IfMatch, out var expectedVersion))
        {
            throw new PreconditionRequiredException(
                "precondition.if_match_required",
                "A valid If-Match header containing the loaded membership version is required.");
        }

        var membership = await dispatcher.SendAsync(
            new UpdateMemberRolesCommand(
                new TenantId(tenantId),
                new MembershipId(membershipId),
                request.Roles ?? [],
                expectedVersion,
                httpContext.TraceIdentifier),
            cancellationToken);
        httpContext.Response.Headers.ETag = $"\"{membership.Version}\"";
        return Results.Ok(ToListItemResponse(membership));
    }

    private static async Task<IResult> SuspendAsync(
        Guid tenantId,
        Guid membershipId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var membership = await dispatcher.SendAsync(
            new SuspendMemberCommand(
                new TenantId(tenantId),
                new MembershipId(membershipId),
                httpContext.TraceIdentifier),
            cancellationToken);
        return Results.Ok(ToListItemResponse(membership));
    }

    private static async Task<IResult> ReactivateAsync(
        Guid tenantId,
        Guid membershipId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var membership = await dispatcher.SendAsync(
            new ReactivateMemberCommand(
                new TenantId(tenantId),
                new MembershipId(membershipId),
                httpContext.TraceIdentifier),
            cancellationToken);
        return Results.Ok(ToListItemResponse(membership));
    }

    private static async Task<IResult> RemoveAsync(
        Guid tenantId,
        Guid membershipId,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var membership = await dispatcher.SendAsync(
            new RemoveMemberCommand(
                new TenantId(tenantId),
                new MembershipId(membershipId),
                httpContext.TraceIdentifier),
            cancellationToken);
        return Results.Ok(ToListItemResponse(membership));
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? state = null,
        string? search = null)
    {
        var list = await dispatcher.QueryAsync(
            new ListMembershipsQuery(
                new TenantId(tenantId),
                MembershipViewStates.Parse(state),
                search),
            cancellationToken);

        return Results.Ok(new MembershipListResponse(
            list.Items.Select(ToListItemResponse).ToList(),
            new MembershipCountsResponse(
                list.Counts.Active,
                list.Counts.Pending,
                list.Counts.Expired,
                list.Counts.Suspended,
                list.Counts.Removed,
                list.Counts.Total)));
    }

    private static async Task<IResult> InviteAsync(
        Guid tenantId,
        MembershipInviteRequest request,
        IRequestDispatcher dispatcher,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var membership = await dispatcher.SendAsync(
            new InviteMemberCommand(
                new TenantId(tenantId),
                request.Email,
                request.Roles ?? [],
                httpContext.TraceIdentifier),
            cancellationToken);

        return Results.Created(
            $"/api/v1/tenants/{tenantId}/memberships/{membership.Id}",
            ToResponse(membership, request.Email));
    }

    private static MembershipResponse ToResponse(MembershipDto membership, string email) =>
        new(
            membership.Id.Value,
            membership.UserId,
            email,
            membership.TenantId.Value,
            membership.State.ToString(),
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);

    private static MembershipListItemResponse ToListItemResponse(MembershipListItemDto membership) =>
        new(
            membership.Id.Value,
            membership.UserId,
            membership.Email,
            membership.TenantId.Value,
            membership.State.ToString(),
            membership.Roles,
            membership.InvitedAt,
            membership.AcceptedAt,
            membership.ExpiresAt,
            membership.Version);
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

public sealed record MembershipInviteRequest(
    string Email,
    IReadOnlyCollection<string>? Roles);

public sealed record MembershipRolesUpdateRequest(IReadOnlyCollection<string>? Roles);

public sealed record MembershipResponse(
    Guid Id,
    Guid UserId,
    string Email,
    Guid TenantId,
    string State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version);

public sealed record MembershipListItemResponse(
    Guid Id,
    Guid UserId,
    string? Email,
    Guid TenantId,
    string State,
    IReadOnlyCollection<string> Roles,
    DateTimeOffset InvitedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset ExpiresAt,
    long Version);

/// <summary>
/// El listado y, al lado, cuántos hay de cada estado.
/// </summary>
/// <remarks>
/// Los conteos viajan con la lista porque quien filtra necesita saber qué hay del otro
/// lado del filtro: sin ellos, ver "0 vencidas" exige pedir cada estado por separado. Se
/// cuentan dentro de lo buscado y antes de aplicar el estado, así que suman el total del
/// roster —o el de la búsqueda— y no el de lo que se está mostrando.
/// </remarks>
public sealed record MembershipListResponse(
    IReadOnlyList<MembershipListItemResponse> Items,
    MembershipCountsResponse Counts);

public sealed record MembershipCountsResponse(
    int Active,
    int Pending,
    int Expired,
    int Suspended,
    int Removed,
    int Total);
