using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using BuildingBlocks.Application;
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
            .RequireAuthorization(TenancyPermissions.MembershipInvite)
            .Accepts<MembershipInviteRequest>("application/json")
            .Produces<MembershipResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/", ListAsync)
            .RequireAuthorization(TenancyPermissions.MembershipRead)
            .Produces<IReadOnlyList<MembershipListItemResponse>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{membershipId:guid}/suspend", SuspendAsync)
            .RequireAuthorization(TenancyPermissions.MembershipManage)
            .Produces<MembershipListItemResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{membershipId:guid}/remove", RemoveAsync)
            .RequireAuthorization(TenancyPermissions.MembershipManage)
            .Produces<MembershipListItemResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPatch("/{membershipId:guid}/roles", UpdateRolesAsync)
            .RequireAuthorization(TenancyPermissions.MembershipManage)
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
        CancellationToken cancellationToken)
    {
        var memberships = await dispatcher.QueryAsync(
            new ListMembershipsQuery(new TenantId(tenantId)),
            cancellationToken);
        return Results.Ok(memberships.Select(ToListItemResponse).ToList());
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
