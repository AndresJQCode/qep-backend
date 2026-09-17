using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Modules.Storage.Domain;

namespace Modules.Storage.Infrastructure.Persistence;

/// <summary>
/// Storage retiene a un usuario mientras sea dueño de algún archivo
/// (<see cref="FileOwnerType.User"/> o <see cref="FileOwnerType.PaymentProof"/> + <c>owner_id</c>).
/// Cualquier estado cuenta, incluso borrado lógico o purgado: la fila sigue nombrando al dueño, y
/// el purgado físico es de otro proceso.
/// </summary>
/// <remarks>
/// Spec 2026-09-16, D18: el frontend sube los comprobantes de pago con <c>ownerId</c> = el usuario
/// que los carga. Pasarlos de <c>User</c> a <c>PaymentProof</c> no puede dejar de retenerlo.
/// </remarks>
internal sealed class FileUserReferenceProbe(StorageDbContext dbContext) : IUserReferenceProbe
{
    public string Source => "storage";

    // Con ||, no con un patrón `is ... or ...`: un árbol de expresión no acepta patrones.
    public Task<bool> HasReferencesAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.FileResources.AnyAsync(
            file => (file.OwnerType == FileOwnerType.User || file.OwnerType == FileOwnerType.PaymentProof)
                && file.OwnerId == userId,
            cancellationToken);
}
