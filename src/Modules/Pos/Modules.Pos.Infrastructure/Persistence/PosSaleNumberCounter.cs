namespace Modules.Pos.Infrastructure.Persistence;

// Sólo existe para que la migración cree la tabla: se lee y escribe con SQL crudo
// (PosSaleNumberGenerator), nunca por el ChangeTracker.
internal sealed class PosSaleNumberCounter
{
    public Guid TenantId { get; init; }

    public long NextValue { get; init; }
}
