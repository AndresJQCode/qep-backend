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
        IReadOnlyCollection<FileOwnerType> disabled = modules is null ? [] : FileOwnerModules.OwnerTypesDisabledIn(modules);

        // Fix round 1 del Bloque D: los dueños de sistema no se listan nunca, ni con el tenant simulado.
        // I-2: lo mismo con el permiso extra del dueño (el export de WMS pide wms.stock.export): sin él ni se lista.
        IReadOnlyCollection<FileOwnerType> excludedOwnerTypes =
        [
            .. disabled
                .Union(SystemOwnedFiles.OwnerTypes)
                .Union(FileOwnerModules.OwnerTypesLackingPermission(executionContext.HasPermission))
        ];

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
