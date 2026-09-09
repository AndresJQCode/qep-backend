using BuildingBlocks.Application;
using Modules.Tenancy.Application;

namespace Modules.Platform.Application;

/// <summary>
/// El log de la aplicación: cada POST, PUT, PATCH o DELETE que se cayó, con su error entero.
/// </summary>
public sealed record ListRequestFailuresQuery(
    Guid TenantId,
    string? Module,
    string? ErrorCode,
    string? TraceId,
    DateTimeOffset? OccurredFrom,
    DateTimeOffset? OccurredTo,
    int Page,
    int PageSize) : IQuery<RequestFailurePage>;

/// <param name="Modules">Los módulos presentes en el log del tenant, para el desplegable del
/// filtro. Viajan con la página y no en un endpoint aparte: se pintan junto a la tabla y separarlos
/// sería una segunda vuelta para llenar una caja de texto.</param>
public sealed record RequestFailurePage(
    IReadOnlyList<RequestFailureDto> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> ErrorCodes);

/// <param name="Detail">La excepción entera con su traza. Es el campo por el que existe el log.</param>
/// <param name="TraceId">Lo que conecta esta fila con las líneas del log de la aplicación.</param>
public sealed record RequestFailureDto(
    Guid Id,
    string Method,
    string Path,
    string Module,
    int StatusCode,
    string? ErrorCode,
    string Message,
    string Detail,
    Guid? SubjectId,
    string? TraceId,
    DateTimeOffset OccurredAt);

public static class RequestFailurePaging
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public static int NormalizePage(int page) => page < 1 ? 1 : page;

    public static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => DefaultPageSize,
        > MaxPageSize => MaxPageSize,
        _ => pageSize
    };
}

public sealed class ListRequestFailuresHandler(
    IRequestFailureRepository repository,
    IExecutionContext executionContext)
    : IQueryHandler<ListRequestFailuresQuery, RequestFailurePage>
{
    public async Task<RequestFailurePage> HandleAsync(
        ListRequestFailuresQuery query,
        CancellationToken cancellationToken)
    {
        PlatformAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, PlatformPermissions.RequestLogRead);

        var page = RequestFailurePaging.NormalizePage(query.Page);
        var pageSize = RequestFailurePaging.NormalizePageSize(query.PageSize);

        var (failures, total) = await repository.SearchAsync(
            query.TenantId,
            Normalize(query.Module),
            Normalize(query.ErrorCode),
            Normalize(query.TraceId),
            query.OccurredFrom,
            query.OccurredTo,
            page,
            pageSize,
            cancellationToken);

        var (modules, errorCodes) = await repository.ListFilterValuesAsync(
            query.TenantId, cancellationToken);

        var items = failures
            .Select(failure => new RequestFailureDto(
                failure.Id.Value,
                failure.Method,
                failure.Path,
                failure.Module,
                failure.StatusCode,
                failure.ErrorCode,
                failure.Message,
                failure.Detail,
                failure.SubjectId,
                failure.TraceId,
                failure.OccurredAt))
            .ToArray();

        return new RequestFailurePage(items, total, page, pageSize, modules, errorCodes);
    }

    // Una cadena vacia en la query string es "sin filtro", no "filtrar por vacio": `?module=`
    // llega asi cuando el cliente limpia el desplegable.
    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
