using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Messaging.Application;

namespace Modules.Messaging.Api;

/// <summary>
/// Spec 2026-10-11 §6.2: las campañas, bajo <c>/api/v1/tenants/{tenantId:guid}/messaging/campaigns</c>. Todas con
/// <c>messaging.campaign.manage</c>; los handlers revalidan tenant, permiso y módulo (403 con código, nunca 404 ante otro tenant).
/// La versión viaja en el cuerpo (<c>expectedVersion</c>), no en <c>If-Match</c>: así lo implementó la pantalla.
/// </summary>
internal static class CampaignEndpoints
{
    public static void MapCampaignEndpoints(this RouteGroupBuilder messaging)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        var group = messaging.MapGroup("/campaigns");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(MessagingPermissions.CampaignManage)
            .Produces<CampaignPageDto<CampaignSummaryDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/{campaignId:guid}", GetAsync)
            .RequireAuthorization(MessagingPermissions.CampaignManage)
            .Produces<CampaignDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{campaignId:guid}/recipients", ListRecipientsAsync)
            .RequireAuthorization(MessagingPermissions.CampaignManage)
            .Produces<CampaignPageDto<CampaignRecipientDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/audience-preview", PreviewAsync)
            .RequireAuthorization(MessagingPermissions.CampaignManage)
            .Accepts<AudiencePreviewRequest>("application/json")
            .Produces<AudiencePreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(MessagingPermissions.CampaignManage)
            .Accepts<CampaignRequest>("application/json")
            .Produces<CampaignDetailDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/{campaignId:guid}", UpdateAsync)
            .RequireAuthorization(MessagingPermissions.CampaignManage)
            .Accepts<CampaignRequest>("application/json")
            .Produces<CampaignDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{campaignId:guid}", DeleteAsync)
            .RequireAuthorization(MessagingPermissions.CampaignManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{campaignId:guid}/launch", LaunchAsync)
            .RequireAuthorization(MessagingPermissions.CampaignManage)
            .Accepts<CampaignVersionRequest>("application/json")
            .Produces<CampaignDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{campaignId:guid}/cancel", CancelAsync)
            .RequireAuthorization(MessagingPermissions.CampaignManage)
            .Accepts<CampaignVersionRequest>("application/json")
            .Produces<CampaignDetailDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> ListAsync(Guid tenantId, int? page, int? pageSize, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListCampaignsQuery(tenantId, page, pageSize), cancellationToken));

    private static async Task<IResult> GetAsync(Guid tenantId, Guid campaignId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCampaignQuery(tenantId, campaignId), cancellationToken));

    private static async Task<IResult> ListRecipientsAsync(
        Guid tenantId, Guid campaignId, string? status, int? page, int? pageSize, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListCampaignRecipientsQuery(tenantId, campaignId, status, page, pageSize), cancellationToken));

    private static async Task<IResult> PreviewAsync(
        Guid tenantId, AudiencePreviewRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new PreviewAudienceQuery(tenantId, request.TagIds, request.CustomerIds, request.TemplateCategory), cancellationToken));

    private static async Task<IResult> CreateAsync(Guid tenantId, CampaignRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var campaign = await dispatcher.SendAsync(
            new CreateCampaignCommand(
                tenantId, request.Name, request.ConnectionId, request.Template?.Name, request.Template?.Language, Parameters(request),
                request.Audience?.TagIds, request.Audience?.CustomerIds, request.ScheduledAt),
            cancellationToken);
        return Results.Created($"/api/v1/tenants/{tenantId}/messaging/campaigns/{campaign.Id}", campaign);
    }

    private static async Task<IResult> UpdateAsync(
        Guid tenantId, Guid campaignId, CampaignRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateCampaignCommand(
                tenantId, campaignId, request.ExpectedVersion, request.Name, request.ConnectionId, request.Template?.Name, request.Template?.Language,
                Parameters(request), request.Audience?.TagIds, request.Audience?.CustomerIds, request.ScheduledAt),
            cancellationToken));

    private static async Task<IResult> DeleteAsync(Guid tenantId, Guid campaignId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteCampaignCommand(tenantId, campaignId), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> LaunchAsync(
        Guid tenantId, Guid campaignId, CampaignVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new LaunchCampaignCommand(tenantId, campaignId, request.ExpectedVersion), cancellationToken));

    private static async Task<IResult> CancelAsync(
        Guid tenantId, Guid campaignId, CampaignVersionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new CancelCampaignCommand(tenantId, campaignId, request.ExpectedVersion), cancellationToken));

    private static Dictionary<string, CampaignParameterInput?>? Parameters(CampaignRequest request) =>
        request.Parameters?.ToDictionary(pair => pair.Key, pair => pair.Value is null ? null : new CampaignParameterInput(pair.Value.Source, pair.Value.Value), StringComparer.Ordinal);
}

/// <summary>§6.2: <c>CampaignInput</c> más <c>expectedVersion</c> (sólo en el <c>PUT</c>). <c>scheduledAt</c> ausente es «enviar al lanzar».</summary>
public sealed record CampaignRequest(
    string? Name,
    Guid? ConnectionId,
    CampaignTemplateRequest? Template,
    Dictionary<string, CampaignParameterRequest?>? Parameters,
    CampaignAudienceRequest? Audience,
    DateTimeOffset? ScheduledAt,
    long? ExpectedVersion = null);

public sealed record CampaignTemplateRequest(string? Name, string? Language);

public sealed record CampaignParameterRequest(string? Source, string? Value);

public sealed record CampaignAudienceRequest(IReadOnlyList<Guid>? TagIds, IReadOnlyList<Guid>? CustomerIds);

/// <summary><c>launch</c> y <c>cancel</c>: <c>{ expectedVersion }</c>.</summary>
public sealed record CampaignVersionRequest(long? ExpectedVersion);

/// <summary><c>audience-preview</c>: el cuerpo es plano.</summary>
public sealed record AudiencePreviewRequest(IReadOnlyList<Guid>? TagIds, IReadOnlyList<Guid>? CustomerIds, string? TemplateCategory);
