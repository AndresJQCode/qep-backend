using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.Persistence;

/// <summary>
/// Storage retiene a un usuario mientras sea dueño de algún archivo
/// (<see cref="FileOwnerType.User"/> + <c>owner_id</c>). Cualquier estado cuenta, incluso
/// borrado lógico o purgado: la fila sigue nombrando al dueño, y el purgado físico es de
/// otro proceso.
/// </summary>
internal sealed class FileUserReferenceProbe(StorageDbContext dbContext) : IUserReferenceProbe
{
    public string Source => "storage";

    public Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.FileResources.AnyAsync(
            file => file.OwnerType == FileOwnerType.User && file.OwnerId == userId,
            cancellationToken);
}
