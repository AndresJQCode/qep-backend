using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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

        // Spec 2026-11-23 §4: la galería de plantillas del sistema. En el grupo y no bajo /agents: una plantilla no es un agente.
        group.MapGet("/agent-templates", ListAgentTemplatesAsync)
            .RequireAuthorization(AiPermissions.AgentManage)
            .Produces<IReadOnlyList<AgentTemplateDto>>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // Spec §5 y plan 2026-11-12 Tarea 9: el historial de sesiones y la actividad del agente en una conversación. Lectura con
        // ai.agent.read; si la actividad también la ve todo asesor de la conversación es DECISIÓN-PENDIENTE P-10.
        group.MapGet("/sessions", ListSessionsAsync)
            .RequireAuthorization(AiPermissions.AgentRead)
            .Produces<AgentSessionListResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/conversations/{conversationId:guid}/agent-activity", GetConversationActivityAsync)
            .RequireAuthorization(AiPermissions.AgentRead)
            .Produces<AgentActivityDto>()
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

        // Spec 2026-11-17 importar/exportar §4. El tope es metadato que Kestrel aplica; los campos ya tienen los suyos en el validador.
        agents.MapPost("/import", ImportAgentAsync)
            .RequireAuthorization(AiPermissions.AgentManage)
            .WithMetadata(new RequestSizeLimitAttribute(AgentLimits.ImportBodyMaxBytes))
            .Accepts<ImportAgentRequest>("application/json")
            .Produces<AgentEditorDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Spec 2026-11-23 §4: crear con una plantilla del sistema. /agents/from-template no choca con /agents/{agentId:guid}.
        agents.MapPost("/from-template", CreateAgentFromTemplateAsync)
            .RequireAuthorization(AiPermissions.AgentManage)
            .Accepts<CreateAgentFromTemplateRequest>("application/json")
            .Produces<AgentEditorDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        agents.MapGet("/{agentId:guid}/export", ExportAgentAsync)
            .RequireAuthorization(AiPermissions.AgentManage)
            .Produces<AgentExportDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

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

        MapCopilot(group.MapGroup("/copilot"));
        return endpoints;
    }

    /// <summary>Spec 2026-11-17 (copiloto) §8: <c>/ai/copilot</c>. Cada handler revalida tenant, permiso y la capacidad <c>copilot</c> (no la
    /// <c>ai</c>, que exige Mensajería).</summary>
    private static void MapCopilot(RouteGroupBuilder copilot)
    {
        copilot.MapGet("/settings", GetCopilotSettingsAsync)
            .RequireAuthorization(AiPermissions.CopilotManage)
            .Produces<CopilotSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        copilot.MapPut("/settings", UpdateCopilotSettingsAsync)
            .RequireAuthorization(AiPermissions.CopilotManage)
            .Accepts<UpdateCopilotSettingsRequest>("application/json")
            .Produces<CopilotSettingsDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        copilot.MapGet("/status", GetCopilotStatusAsync)
            .RequireAuthorization(AiPermissions.CopilotUse)
            .Produces<CopilotStatusDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // D-C7 y D-C8: los hilos privados del miembro. Preguntar responde sin esperar al modelo (202 al seguir el hilo, 201 al crearlo);
        // el frontend sondea GET mientras pending. Un hilo de otro miembro es 404 ai.copilot.thread_not_found, nunca 403.
        var threads = copilot.MapGroup("/threads");

        threads.MapGet("/", ListCopilotThreadsAsync)
            .RequireAuthorization(AiPermissions.CopilotUse)
            .Produces<CopilotThreadPage>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        threads.MapPost("/", CreateCopilotThreadAsync)
            .WithCopilotAsking()
            .Accepts<CopilotQuestionRequest>("application/json")
            .Produces<CopilotThreadDto>(StatusCodes.Status201Created);

        threads.MapGet("/{threadId:guid}", GetCopilotThreadAsync)
            .RequireAuthorization(AiPermissions.CopilotUse)
            .Produces<CopilotThreadDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        threads.MapPut("/{threadId:guid}", RenameCopilotThreadAsync)
            .RequireAuthorization(AiPermissions.CopilotUse)
            .Accepts<RenameCopilotThreadRequest>("application/json")
            .Produces<CopilotThreadDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        threads.MapDelete("/{threadId:guid}", DeleteCopilotThreadAsync)
            .RequireAuthorization(AiPermissions.CopilotUse)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        threads.MapPost("/{threadId:guid}/messages", SendCopilotMessageAsync)
            .WithCopilotAsking()
            .Accepts<CopilotQuestionRequest>("application/json")
            .Produces<CopilotThreadDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);

        threads.MapPost("/{threadId:guid}/messages/{messageId:guid}/retry", RetryCopilotMessageAsync)
            .WithCopilotAsking()
            .Produces<CopilotThreadDto>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>Lo común de preguntar (crear, enviar, reintentar): 409 busy, 422 disabled o proveedor sin cuenta, 429 con
    /// <c>retryAfterSeconds</c>.</summary>
    private static RouteHandlerBuilder WithCopilotAsking(this RouteHandlerBuilder route) =>
        route.RequireAuthorization(AiPermissions.CopilotUse)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

    private static async Task<IResult> ListCopilotThreadsAsync(
        Guid tenantId, string? cursor, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListCopilotThreadsQuery(tenantId, cursor), cancellationToken));

    // Sin Location: el panel abre con el hilo que vuelve.
    private static async Task<IResult> CreateCopilotThreadAsync(
        Guid tenantId, CopilotQuestionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(new CreateCopilotThreadCommand(tenantId, request.Text ?? string.Empty), cancellationToken));

    private static async Task<IResult> GetCopilotThreadAsync(
        Guid tenantId, Guid threadId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCopilotThreadQuery(tenantId, threadId), cancellationToken));

    private static async Task<IResult> RenameCopilotThreadAsync(
        Guid tenantId, Guid threadId, RenameCopilotThreadRequest request, HttpContext httpContext, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new RenameCopilotThreadCommand(tenantId, threadId, RequireVersion(httpContext), request.Title ?? string.Empty), cancellationToken));

    private static async Task<IResult> DeleteCopilotThreadAsync(
        Guid tenantId, Guid threadId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new DeleteCopilotThreadCommand(tenantId, threadId), cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SendCopilotMessageAsync(
        Guid tenantId, Guid threadId, CopilotQuestionRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Accepted(
            (string?)null,
            await dispatcher.SendAsync(new SendCopilotMessageCommand(tenantId, threadId, request.Text ?? string.Empty), cancellationToken));

    private static async Task<IResult> RetryCopilotMessageAsync(
        Guid tenantId, Guid threadId, Guid messageId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Accepted(
            (string?)null,
            await dispatcher.SendAsync(new RetryCopilotMessageCommand(tenantId, threadId, messageId), cancellationToken));

    private static async Task<IResult> GetCopilotSettingsAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCopilotSettingsQuery(tenantId), cancellationToken));

    // Adenda D-10: el cuerpo son los campos editables de la respuesta del GET; la versión va en If-Match ("0" sin fila todavía).
    private static async Task<IResult> UpdateCopilotSettingsAsync(
        Guid tenantId, UpdateCopilotSettingsRequest request, HttpContext httpContext, IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new UpdateCopilotSettingsCommand(
                tenantId, RequireCopilotVersion(httpContext), request.Enabled, request.Provider, request.Model, request.Instructions,
                request.MonthlyTokenBudget, request.RetentionDays),
            cancellationToken));

    private static async Task<IResult> GetCopilotStatusAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetCopilotStatusQuery(tenantId), cancellationToken));

    /// <summary>Como <see cref="RequireVersion"/>, pero acepta 0: la configuración del copiloto y un hilo recién leído sin fila tienen
    /// versión 0 hasta el primer guardado.</summary>
    private static long RequireCopilotVersion(HttpContext httpContext) =>
        TryParseVersion(httpContext.Request.Headers.IfMatch, out var version, allowZero: true)
            ? version
            : throw new PreconditionRequiredException(
                "precondition.if_match_required", "A valid If-Match header containing the loaded version is required.");

    // Sin Location: el playground abre con la prueba que vuelve. El cuerpo es opcional (sin cliente fijo).
    private static async Task<IResult> CreateTestAsync(
        Guid tenantId, Guid agentId, CreateAgentTestRequest? request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(
                new CreateAgentTestCommand(
                    tenantId, agentId, request?.CustomerId,
                    request?.Block is { } block ? new AgentTestBlock(block.Objective ?? string.Empty, block.Context) : null),
                cancellationToken));

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

    private static bool TryParseVersion(string? etag, out long version, bool allowZero = false)
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

        return long.TryParse(normalized.Trim('"'), out version) && (version > 0 || (allowZero && version == 0));
    }

    private static async Task<IResult> ListConnectionsAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListAiConnectionsQuery(tenantId), cancellationToken));

    private static async Task<IResult> ListAgentTemplatesAsync(Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListAgentTemplatesQuery(tenantId), cancellationToken));

    // Plan 2026-11-17 A5: from y to son días del tenant (yyyy-MM-dd), los dos inclusivos; cursor es el nextCursor de la página anterior.
    private static async Task<IResult> ListSessionsAsync(
        Guid tenantId,
        Guid? agentId,
        DateOnly? from,
        DateOnly? to,
        string? cursor,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListAgentSessionsQuery(tenantId, agentId, from, to, cursor), cancellationToken));

    private static async Task<IResult> GetConversationActivityAsync(
        Guid tenantId, Guid conversationId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new GetConversationAgentActivityQuery(tenantId, conversationId), cancellationToken));

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

    // Sin Location, como crear: el editor abre con el agente que vuelve. Un export nulo lo frena el validador.
    private static async Task<IResult> ImportAgentAsync(
        Guid tenantId, ImportAgentRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(
                new ImportAgentCommand(tenantId, request.Name ?? request.Export?.Name ?? string.Empty, request.Export!), cancellationToken));

    // Sin Location, como crear e importar. Un cuerpo sin campos lo frena el validador con el campo (422), no un 500.
    private static async Task<IResult> CreateAgentFromTemplateAsync(
        Guid tenantId, CreateAgentFromTemplateRequest request, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Created(
            (string?)null,
            await dispatcher.SendAsync(
                new CreateAgentFromTemplateCommand(tenantId, request.TemplateId ?? string.Empty, request.Name ?? string.Empty), cancellationToken));

    private static async Task<IResult> ExportAgentAsync(Guid tenantId, Guid agentId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ExportAgentQuery(tenantId, agentId), cancellationToken));

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

/// <summary><c>name</c> (si viene) pisa el del archivo.</summary>
public sealed record ImportAgentRequest(string? Name, AgentExportDto? Export);

public sealed record CreateAgentFromTemplateRequest(string? TemplateId, string? Name);

public sealed record UpdateAgentRequest(string Name, IReadOnlyList<Guid>? ConnectionIds);

/// <summary>§7: <c>customerId</c> fija un cliente del tenant para <c>get_order_status</c>; sin él, la herramienta responde
/// <c>customer_unknown</c>. <c>block</c> (spec 2026-11-17 §7) prueba el agente como dentro de un bloque «Agente IA» de un chatbot.</summary>
public sealed record CreateAgentTestRequest(Guid? CustomerId, AgentTestBlockRequest? Block = null);

/// <summary>Spec 2026-11-17 §7: el paso del chatbot que simula la prueba; <c>objective</c> 1..2000 y <c>context</c> hasta 2000.</summary>
public sealed record AgentTestBlockRequest(string? Objective, string? Context);

public sealed record SendAgentTestMessageRequest(string Text);

/// <summary>§8: <c>{ text }</c> de crear un hilo y de preguntar en uno.</summary>
public sealed record CopilotQuestionRequest(string? Text);

/// <summary>§8: <c>{ title }</c>, con <c>If-Match</c>.</summary>
public sealed record RenameCopilotThreadRequest(string? Title);

/// <summary>Adenda D-10 del plan fase 2: exactamente lo que manda el frontend (<c>saveCopilotSettings</c>).</summary>
public sealed record UpdateCopilotSettingsRequest(
    bool Enabled, string? Provider, string? Model, string? Instructions, long? MonthlyTokenBudget, int RetentionDays);
