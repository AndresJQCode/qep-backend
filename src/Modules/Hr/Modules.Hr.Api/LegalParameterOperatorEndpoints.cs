using System.Globalization;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Modules.Hr.Application;
using Modules.Tenancy.Domain;

namespace Modules.Hr.Api;

/// <summary>
/// La consola de valores legales del operador (spec 2026-11-22, «API»). La ruta lleva el tenant desde el que actúa el operador, como la
/// de unidades tributarias. Doble capa: la política exige <c>operator.legal_parameters.*</c> y el handler revalida con
/// <c>OperatorAuthorization</c>. <c>{key}</c> y <c>{effectiveFrom}</c> llegan como texto y los lee el handler, después de autorizar.
/// </summary>
public static class LegalParameterOperatorEndpoints
{
    private const int DefaultPageSize = 50;
    private const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static IEndpointRouteBuilder MapLegalParameterOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1/tenants/{tenantId:guid}/operator/legal-parameters")
            .WithTags("Operator");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(HrOperatorPermissions.LegalParametersRead)
            .Produces<LegalParametersDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/history", HistoryAsync)
            .RequireAuthorization(HrOperatorPermissions.LegalParametersRead)
            .Produces<LegalParameterHistoryPageDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/{key}/{effectiveFrom}", SetAsync)
            .RequireAuthorization(HrOperatorPermissions.LegalParametersManage)
            .Accepts<SetLegalParameterRequest>("application/json")
            .Produces<LegalParametersDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/{key}/{effectiveFrom}", DeleteAsync)
            .RequireAuthorization(HrOperatorPermissions.LegalParametersManage)
            .Produces<LegalParametersDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPut("/brackets/{key}/{effectiveFrom}", SetBracketsAsync)
            .RequireAuthorization(HrOperatorPermissions.LegalParametersManage)
            .Accepts<SetLegalBracketsRequest>("application/json")
            .Produces<LegalParametersDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapDelete("/brackets/{key}/{effectiveFrom}", DeleteBracketsAsync)
            .RequireAuthorization(HrOperatorPermissions.LegalParametersManage)
            .Produces<LegalParametersDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/import/template", GetImportTemplateAsync)
            .RequireAuthorization(HrOperatorPermissions.LegalParametersManage)
            .Produces(StatusCodes.Status200OK, contentType: ExcelContentType)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/import/preview", PreviewImportAsync)
            .RequireAuthorization(HrOperatorPermissions.LegalParametersManage)
            .Produces<LegalParameterImportPreviewDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        group.MapPost("/import", ImportAsync)
            .RequireAuthorization(HrOperatorPermissions.LegalParametersManage)
            .Produces<LegalParameterImportResultDto>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .DisableAntiforgery();

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(new ListLegalParametersQuery(new TenantId(tenantId)), cancellationToken));

    // La página llega como texto: un valor que el binder no entiende lanzaría una BadHttpRequestException y ApiExceptionHandler la
    // reduce a 500. Uno ilegible vuelve al valor por defecto; el handler acota el rango.
    private static async Task<IResult> HistoryAsync(
        Guid tenantId, string? page, string? pageSize, IRequestDispatcher dispatcher, CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.QueryAsync(
            new ListLegalParameterHistoryQuery(
                new TenantId(tenantId), ReadInt(page, 1), ReadInt(pageSize, DefaultPageSize)),
            cancellationToken));

    // Un value ausente llega como 0 y lo rechaza el dominio (value_invalid), no el binder.
    private static async Task<IResult> SetAsync(
        Guid tenantId,
        string key,
        string effectiveFrom,
        SetLegalParameterRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SetLegalParameterCommand(new TenantId(tenantId), key, effectiveFrom, request.Value ?? 0m),
            cancellationToken));

    private static async Task<IResult> DeleteAsync(
        Guid tenantId,
        string key,
        string effectiveFrom,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new DeleteLegalParameterCommand(new TenantId(tenantId), key, effectiveFrom), cancellationToken));

    // Sin lista de tramos llega vacía y la rechaza el dominio (value_invalid): al menos un tramo.
    private static async Task<IResult> SetBracketsAsync(
        Guid tenantId,
        string key,
        string effectiveFrom,
        SetLegalBracketsRequest request,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new SetLegalBracketsCommand(
                new TenantId(tenantId),
                key,
                effectiveFrom,
                (request.Brackets ?? [])
                    .Select(bracket => new Modules.Hr.Domain.LegalBracketInput(bracket.LowerBound, bracket.UpperBound, bracket.Rate))
                    .ToList()),
            cancellationToken));

    private static async Task<IResult> DeleteBracketsAsync(
        Guid tenantId,
        string key,
        string effectiveFrom,
        IRequestDispatcher dispatcher,
        CancellationToken cancellationToken) =>
        Results.Ok(await dispatcher.SendAsync(
            new DeleteLegalBracketsCommand(new TenantId(tenantId), key, effectiveFrom), cancellationToken));

    private static async Task<IResult> GetImportTemplateAsync(
        Guid tenantId, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var template = await dispatcher.QueryAsync(
            new GetLegalParameterImportTemplateQuery(new TenantId(tenantId)), cancellationToken);

        return Results.File(template.Content, ExcelContentType, template.FileName);
    }

    // IFormFile anulable: sin archivo el handler responde 422 hr.legal_parameter.import.file_required y no un 400 del binding.
    private static async Task<IResult> PreviewImportAsync(
        Guid tenantId, IFormFile? file, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;
        var preview = await dispatcher.SendAsync(
            new PreviewLegalParameterImportCommand(
                new TenantId(tenantId), file?.FileName ?? string.Empty, file?.Length ?? 0, content),
            cancellationToken);

        return Results.Ok(preview);
    }

    private static async Task<IResult> ImportAsync(
        Guid tenantId, IFormFile? file, IRequestDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await using var content = file?.OpenReadStream() ?? Stream.Null;
        var result = await dispatcher.SendAsync(
            new ImportLegalParametersCommand(
                new TenantId(tenantId), file?.FileName ?? string.Empty, file?.Length ?? 0, content),
            cancellationToken);

        return Results.Ok(result);
    }

    private static int ReadInt(string? raw, int fallback) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

public sealed record SetLegalParameterRequest(decimal? Value);

public sealed record LegalBracketRequest(decimal LowerBound, decimal? UpperBound, decimal Rate);

public sealed record SetLegalBracketsRequest(IReadOnlyList<LegalBracketRequest>? Brackets);
