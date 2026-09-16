using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Modules.Storage.Infrastructure.Persistence;

/// <summary>
/// Storage retiene un objeto público mientras algún archivo lo tenga como
/// <c>PublicStorageKey</c> (spec 2026-09-16, D12): un comprobante v2 ya movido. Cualquier estado
/// cuenta, igual que en <see cref="FileUserReferenceProbe"/>.
/// </summary>
internal sealed class FilePublicObjectReferenceProbe(StorageDbContext dbContext)
    : IPublicObjectReferenceProbe
{
    public string Source => "storage";

    public Task<bool> HasReferencesAsync(string publicStorageKey, CancellationToken cancellationToken) =>
        dbContext.FileResources.AnyAsync(
            file => file.PublicStorageKey == publicStorageKey,
            cancellationToken);
}
