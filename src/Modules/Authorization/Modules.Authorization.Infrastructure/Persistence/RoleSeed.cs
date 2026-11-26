namespace Modules.Authorization.Infrastructure.Persistence;

/// <summary>
/// La marca de un tenant al que ya se le sembraron los roles (spec 2026-11-25, roles por tenant).
/// </summary>
/// <remarks>
/// Es sólo persistencia: con la fila no se vuelve a sembrar, así un rol borrado no revive y uno editado no se pisa.
/// </remarks>
internal sealed class RoleSeed
{
    public Guid TenantId { get; init; }

    public DateTimeOffset SeededAt { get; init; }
}
