using BuildingBlocks.Application;
using FluentValidation;
using FluentValidation.Results;
using Modules.ApiAccess.Domain;
using Modules.Tenancy.Application;

namespace Modules.ApiAccess.Application;

public sealed record ApiKeyRequestDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid ApiKeyId,
    string ApiKeyName,
    string Method,
    string Path,
    int StatusCode,
    string? ErrorCode,
    Guid? MessageId,
    string? TraceId,
    int DurationMs,
    ApiMessageState? Message);

public sealed record ApiKeyRequestPage(IReadOnlyList<ApiKeyRequestDto> Items, int Total, int Page, int PageSize);

public sealed record ListApiKeyRequestsQuery(
    Guid TenantId,
    Guid? ApiKeyId,
    string? Result,
    string? TraceId,
    DateTimeOffset? OccurredFrom,
    DateTimeOffset? OccurredTo,
    int Page,
    int PageSize) : IQuery<ApiKeyRequestPage>;

public static class ApiKeyRequestPaging
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public static int NormalizePage(int page) => Math.Max(1, page);

    // Un tamaño fuera de rango no es un error: la pantalla pide lo que cabe y recibe lo permitido.
    public static int NormalizePageSize(int pageSize) =>
        pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
}

internal static class ApiKeyRequestFilter
{
    // Vacío es «sin filtro»; un valor que no es ninguno de los dos se rechaza con el campo, no se ignora:
    // ignorarlo mostraría todo bajo un filtro que la pantalla cree activo.
    internal static ApiKeyRequestResult? ParseResult(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "success" => ApiKeyRequestResult.Success,
            "failure" => ApiKeyRequestResult.Failure,
            _ => throw new ValidationException(
            [
                new ValidationFailure("result", "El resultado tiene que ser success o failure.")
            ])
        };
    }

    internal static string? NormalizeTraceId(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= ApiKeyRequest.TraceIdMaxLength
            ? trimmed
            : trimmed[..ApiKeyRequest.TraceIdMaxLength];
    }
}

public sealed class ListApiKeyRequestsHandler(
    IApiKeyRequestRepository repository,
    IApiMessageStates messageStates,
    IExecutionContext executionContext,
    ITenantModules tenantModules,
    IClock clock)
    : IQueryHandler<ListApiKeyRequestsQuery, ApiKeyRequestPage>
{
    public async Task<ApiKeyRequestPage> HandleAsync(
        ListApiKeyRequestsQuery query,
        CancellationToken cancellationToken)
    {
        await ApiAccessAuthorization.EnsureAsync(
            executionContext, tenantModules, query.TenantId, ApiAccessPermissions.ApiKeyManage, cancellationToken);

        var result = ApiKeyRequestFilter.ParseResult(query.Result);
        var page = ApiKeyRequestPaging.NormalizePage(query.Page);
        var pageSize = ApiKeyRequestPaging.NormalizePageSize(query.PageSize);

        // Sin filtro de resultado no hace falta saber qué mensajes fallaron. Con él, desde el inicio del
        // rango o, si no hay, desde que el registro empieza a purgar: más atrás no hay filas.
        IReadOnlyList<Guid> failedIds = result is null
            ? []
            : await messageStates.ListFailedIdsAsync(
                query.TenantId,
                query.OccurredFrom ?? clock.UtcNow.AddDays(-ApiKeyRequest.RetentionDays),
                cancellationToken);

        var (items, total) = await repository.SearchAsync(
            new ApiKeyRequestSearch(
                query.TenantId,
                query.ApiKeyId,
                result,
                failedIds,
                ApiKeyRequestFilter.NormalizeTraceId(query.TraceId),
                query.OccurredFrom,
                query.OccurredTo,
                page,
                pageSize),
            cancellationToken);

        var messageIds = items
            .Where(item => item.MessageId is not null)
            .Select(item => item.MessageId!.Value)
            .Distinct()
            .ToArray();
        IReadOnlyDictionary<Guid, ApiMessageState> states = messageIds.Length == 0
            ? new Dictionary<Guid, ApiMessageState>()
            : await messageStates.FindAsync(query.TenantId, messageIds, cancellationToken);

        var dtos = items
            .Select(item => new ApiKeyRequestDto(
                item.Id,
                item.OccurredAt,
                item.ApiKeyId,
                item.ApiKeyName,
                item.Method,
                item.Path,
                item.StatusCode,
                item.ErrorCode,
                item.MessageId,
                item.TraceId,
                item.DurationMs,
                item.MessageId is { } id && states.TryGetValue(id, out var state) ? state : null))
            .ToArray();

        return new ApiKeyRequestPage(dtos, total, page, pageSize);
    }
}
