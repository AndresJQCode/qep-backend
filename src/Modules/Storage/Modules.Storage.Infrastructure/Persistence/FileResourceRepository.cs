using Microsoft.EntityFrameworkCore;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.Persistence;

internal sealed class FileResourceRepository(StorageDbContext dbContext) : IFileResourceRepository
{
    public void Add(FileResource resource) => dbContext.FileResources.Add(resource);

    public async Task<FileResource?> GetAsync(
        FileResourceId id,
        CancellationToken cancellationToken) =>
        await dbContext.FileResources
            .Include(resource => resource.Variants)
            .FirstOrDefaultAsync(resource => resource.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<FileResource> Items, int TotalCount)> SearchAsync(
        Guid tenantId,
        string? search,
        FileResourceStatus? status,
        string? kind,
        string? category,
        string? tag,
        FileOwnerFilter? owner,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.FileResources
            .AsNoTracking()
            .Include(resource => resource.Variants)
            .Where(resource =>
                resource.TenantId == tenantId &&
                resource.Status != FileResourceStatus.Deleted &&
                resource.Status != FileResourceStatus.Purged);

        // CAT-09. Va **después** del filtro de tenant y nunca en su lugar: el owner acota dentro
        // del tenant, no lo reemplaza. Un OwnerId es único de por sí, así que filtrar sólo por él
        // parecería funcionar en las pruebas y publicaría la biblioteca ajena en cuanto dos
        // tenants compartieran un id. Lo cubre CA-CAT-09-06.
        if (owner is { } selectedOwner)
        {
            query = query.Where(resource =>
                resource.OwnerType == selectedOwner.OwnerType &&
                resource.OwnerId == selectedOwner.OwnerId);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(resource =>
                EF.Functions.ILike(resource.Name, $"%{term}%") ||
                (resource.Category != null && EF.Functions.ILike(resource.Category, $"%{term}%")));
        }

        if (status is { } selectedStatus)
        {
            query = query.Where(resource => resource.Status == selectedStatus);
        }
        else
        {
            // Las subidas activas las representa la cola de progreso del cliente. Mantener
            // las filas transitorias fuera de la biblioteca por defecto evita que los PUT
            // fallidos aparezcan indefinidamente como "subiendo".
            query = query.Where(resource => resource.Status != FileResourceStatus.PendingUpload);
        }

        query = kind?.Trim().ToLowerInvariant() switch
        {
            "image" => query.Where(resource => resource.MimeType.StartsWith("image/")),
            "spreadsheet" => query.Where(resource =>
                resource.MimeType == "application/vnd.ms-excel" ||
                resource.MimeType == "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            "document" => query.Where(resource =>
                !resource.MimeType.StartsWith("image/") &&
                resource.MimeType != "application/vnd.ms-excel" &&
                resource.MimeType != "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(category))
        {
            var selectedCategory = category.Trim();
            query = query.Where(resource =>
                resource.Category != null &&
                EF.Functions.ILike(resource.Category, selectedCategory));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var selectedTag = tag.Trim().ToLowerInvariant();
            query = query.Where(resource => resource.Tags.Contains(selectedTag));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(resource => resource.CreatedAt)
            .ThenByDescending(resource => resource.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
        return (items, totalCount);
    }
}
