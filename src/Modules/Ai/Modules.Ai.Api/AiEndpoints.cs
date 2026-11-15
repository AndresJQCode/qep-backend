using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Ai.Application;
using Modules.Ai.Domain;

namespace Modules.Ai.Api;

public static class AiEndpoints
{
    /// <summary>Spec 2026-10-29 §5: todo bajo <c>/api/v1/tenants/{tenantId:guid}/ai</c>. Cada ruta declara su <c>RequireAuthorization</c>;
    /// los handlers revalidan tenant, permiso y capacidad <c>ai</c> (403 con código, nunca 404 ante otro tenant).</summary>
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/ai").WithTags("Ai");

        group.MapGet("/models", ListModelsAsync)
            .RequireAuthorization(AiPermissions.AgentRead)
            .Produces<AiModelCatalogDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/settings", GetSettingsAsync)
            .RequireAuthorization(AiPermissions.AgentRead)
            .Produces<AiSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/connections", ListConnectionsAsync)
            .RequireAuthorization(AiPermissions.AgentRead)
            .Produces<IReadOnlyList<AiConnectionDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var agents = group.MapGroup("/agents");

        agents.MapGet("/", ListAgentsAsync)
            .RequireAuthorization(AiPermissions.AgentRead)
            .Produces<AgentListResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        agents.MapPost("/", CreateAgentAsync)
            .RequireAuthorization(AiPermissions.AgentManage)
            .Accepts<CreateAgentRequest>("application/json")
            .Produces<AgentEditorDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        agents.MapGet("/{agentId:guid}", GetAgentAsync)
            .RequireAuthorization(AiPermissions.AgentRead)
            .Produces<AgentEditorDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        agents.MapPut("/{agentId:guid}", UpdateAgentAsync)
            .WithWriteContract()
            .Accepts<UpdateAgentRequest>("application/json")
            .Produces<AgentEditorDto>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        agents.MapPut("/{agentId:guid}/draft", SaveDraftAsync)
            .WithWriteContract()
            .Accepts<AgentConfig>("application/json")
            .Produces<AgentDraftSavedDto>();

        agents.MapPost("/{agentId:guid}/publish", PublishAsync)
            .WithWriteContract()
            .Produces<AgentEditorDto>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        agents.MapPost("/{agentId:guid}/pause", PauseAsync)
            .WithWriteContract()
            .Produces<AgentEditorDto>();

        agents.MapPost("/{agentId:guid}/resume", ResumeAsync)
            .WithWriteContract()
            .Produces<AgentEditorDto>();

        agents.MapDelete("/{agentId:guid}", DeleteAsync)
            .WithWriteContract()
            .Produces(StatusCodes.Status204NoContent);

        // §7: el playground. Abre una prueba con la foto del borrador; cada mensaje encola un trabajo y responde 202 sin esperar al
        // modelo (el frontend sondea GET mientras pending): así no depende del timeout de los proxys.
        agents.MapPost("/{agentId:guid}/tests", CreateTestAsync)
            .RequireAuthorization(AiPermissions.AgentManage)
            .Accepts<CreateAgentTestRequest>("application/json")
            .Produces<AgentTestDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        var tests = group.MapGroup("/tests");

        tests.MapPost("/{testId:guid}/messages", SendTestMessageAsync)
            .RequireAuthorization(AiPermissions.AgentManage)
            .Accepts<SendAgentTestMessageRequest>("application/json")
            .Produces<AgentTestDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        tests.MapGet("/{testId:guid}", GetTestAsync)
            .RequireAuthorization(AiPermissions.AgentManage)
            .Produces<AgentTestDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tests.MapDelete("/{testId:guid}", DeleteTestAsync)
            .RequireAuthorization(AiPermissions.AgentManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    // Sin Location: el playground abre con la prueba que vuelve. El cuerpo es opcional (sin cliente fijo).
    private static async Task<IResult> CreateTestAsync(
        Guid tenantId, Guid agentId, CreateAgentTestRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(new CreateAgentTestCommand(tenantId, agentId, request?.CustomerId), cancellationToken));

    private static async Task<IResult> SendTestMessageAsync(
        Guid tenantId, Guid testId, SendAgentTestMessageRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Accepted(
            (string?)null,
            await dispatcher.SendAsync(new SendAgentTestMessageCommand(tenantId, testId, request.Text), cancellationToken));

    private static async Task<IResult> GetTestAsync(Guid tenantId, Guid testId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetAgentTestQuery(tenantId, testId), cancellationToken));

    private static async Task<IResult> DeleteTestAsync(
        Guid tenantId, Guid testId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteAgentTestCommand(tenantId, testId), cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Lo común de toda escritura sobre un agente existente: <c>If-Match</c> (428 sin él, 412 si es viejo).</summary>
    private static RouteHandlerBuilder WithWriteContract(this RouteHandlerBuilder route) =>
        route.RequireAuthorization(AiPermissions.AgentManage)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

    // Copia de AutomationsEndpoints.RequireVersion: sin If-Match 428; la versión vieja la responde el handler con 412.
    private static long RequireVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required", "A valid If-Match header containing the loaded agent version is required.");

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

        return long.TryParse(normalized.Trim('"'), out version) && version > 0;
    }

    private static async Task<IResult> ListConnectionsAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListAiConnectionsQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListAgentsAsync(
        Guid tenantId, string? search, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListAgentsQuery(tenantId, search), cancellationToken));

    // Sin Location: el editor abre con el agente que vuelve.
    private static async Task<IResult> CreateAgentAsync(
        Guid tenantId, CreateAgentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(
                new CreateAgentCommand(
                    tenantId, request.Name, request.Source is null ? null! : new AgentSource(request.Source.Kind, request.Source.AgentId)),
                cancellationToken));

    private static async Task<IResult> GetAgentAsync(Guid tenantId, Guid agentId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetAgentQuery(tenantId, agentId), cancellationToken));

    private static async Task<IResult> UpdateAgentAsync(
        Guid tenantId, Guid agentId, UpdateAgentRequest request, HttpContext httpContext, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateAgentCommand(tenantId, agentId, RequireVersion(httpContext), request.Name, request.ConnectionIds ?? []),
            cancellationToken));

    /// <summary>El cuerpo es el documento <c>config</c> entero (§4.6), tal como lo guarda el agente.</summary>
    private static async Task<IResult> SaveDraftAsync(
        Guid tenantId, Guid agentId, AgentConfig config, HttpContext httpContext, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SaveAgentDraftCommand(tenantId, agentId, RequireVersion(httpContext), config), cancellationToken));

    private static async Task<IResult> PublishAsync(
        Guid tenantId, Guid agentId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new PublishAgentCommand(tenantId, agentId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> PauseAsync(
        Guid tenantId, Guid agentId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new PauseAgentCommand(tenantId, agentId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> ResumeAsync(
        Guid tenantId, Guid agentId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(new ResumeAgentCommand(tenantId, agentId, RequireVersion(httpContext)), cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId, Guid agentId, HttpContext httpContext, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteAgentCommand(tenantId, agentId, RequireVersion(httpContext)), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetSettingsAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetAiSettingsQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListModelsAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListAiModelsQuery(tenantId), cancellationToken));
}

/// <summary>§5: <c>source.kind</c> es <c>blank</c> o <c>duplicate</c> (con <c>agentId</c>).</summary>
public sealed record AgentSourceRequest(string Kind, Guid? AgentId);

public sealed record CreateAgentRequest(string Name, AgentSourceRequest Source);

public sealed record UpdateAgentRequest(string Name, IReadOnlyList<Guid>? ConnectionIds);

/// <summary>§7: <c>customerId</c> fija un cliente del tenant para <c>get_order_status</c>; sin él, la herramienta responde
/// <c>customer_unknown</c>.</summary>
public sealed record CreateAgentTestRequest(Guid? CustomerId);

public sealed record SendAgentTestMessageRequest(string Text);
