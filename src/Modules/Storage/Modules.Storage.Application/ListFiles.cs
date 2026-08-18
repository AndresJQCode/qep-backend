using BuildingBlocks.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;

namespace Modules.Storage.Application;

public sealed record ListFilesQuery(
    Guid TenantId,
    string? Search,
    FileResourceStatus? Status,
    string? Kind,
    string? Category,
    string? Tag,
    FileOwnerFilter? Owner,
    int Page,
    int PageSize) : IQuery<PagedFilesDto>;

public sealed class ListFilesHandler(
    IFileResourceRepository repository,
    IPublicObjectStorage publicStorage,
    IExecutionContext executionContext)
    : IQueryHandler<ListFilesQuery, PagedFilesDto>
{
    public async Task<PagedFilesDto> HandleAsync(
        ListFilesQuery query,
        CancellationToken cancellationToken)
    {
        StorageAuthorization.EnsureAuthorized(
            executionContext, query.TenantId, StoragePermissions.FileRead);

        var page = Math.Max(query.Page, 1);
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;
        var (items, totalCount) = await repository.SearchAsync(
            query.TenantId,
            query.Search,
            query.Status,
            query.Kind,
            query.Category,
            query.Tag,
            query.Owner,
            page,
            pageSize,
            cancellationToken);

        return new PagedFilesDto(
            items.Select(item => item.ToDto(publicStorage)).ToArray(),
            totalCount,
            page,
            pageSize);
    }
}
