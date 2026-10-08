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
    ITenantModules tenantModules,
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

        // Spec 2026-10-07: los archivos cuyo dueño es de un módulo apagado no se listan. El filtro va
        // en SQL y no en memoria, para que totalCount y la paginación sigan siendo ciertos. Con el
        // tenant simulado por el stub (null) no se excluye nada.
        var modules = await tenantModules.FindAsync(query.TenantId, cancellationToken);
        IReadOnlyCollection<FileOwnerType> excludedOwnerTypes =
            modules is null ? [] : FileOwnerModules.OwnerTypesDisabledIn(modules);

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
            excludedOwnerTypes,
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
