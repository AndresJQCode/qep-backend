using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using TenantModuleKey = Modules.Tenancy.Domain.TenantModuleKey;
using TenantModuleKeys = Modules.Tenancy.Domain.TenantModuleKeys;
using TenantModuleSet = Modules.Tenancy.Domain.TenantModuleSet;

namespace Modules.Storage.Application;

/// <summary>
/// <c>storage.file.*</c> es núcleo —lo usan el logo del tenant y los comprobantes a la vez—, así que
/// el permiso no sabe de quién es el archivo. Lo sabe <see cref="FileOwnerType"/> (spec 2026-10-07).
/// Un valor <c>null</c> es núcleo. Una prueba exige que las claves sean exactamente
/// <c>Enum.GetValues&lt;FileOwnerType&gt;()</c>: un miembro nuevo sin mapear no pasa.
/// </summary>
public static class FileOwnerModules
{
    public static readonly IReadOnlyDictionary<FileOwnerType, TenantModuleKey?> ByOwnerType =
        new Dictionary<FileOwnerType, TenantModuleKey?>
        {
            // Ninguna pantalla actual lo manda; los comprobantes subidos antes de v2 siguieron siendo
            // User (D13) y quedan como núcleo: residual aceptado (DECISIÓN-PENDIENTE del spec).
            [FileOwnerType.User] = null,
            [FileOwnerType.Entity] = null,
            [FileOwnerType.System] = null,
            [FileOwnerType.Product] = TenantModuleKeys.Catalog,
            [FileOwnerType.PaymentProof] = TenantModuleKeys.Orders,
            [FileOwnerType.Tenant] = null,
        };

    public static TenantModuleKey? ModuleOf(FileOwnerType ownerType) => ByOwnerType[ownerType];

    /// <summary>Los tipos de dueño cuyo módulo no es efectivo: el listado los excluye en SQL.</summary>
    public static IReadOnlyCollection<FileOwnerType> OwnerTypesDisabledIn(TenantModuleSet modules) =>
        ByOwnerType
            .Where(pair => pair.Value is { } key && !modules.IsEnabled(key))
            .Select(pair => pair.Key)
            .ToArray();
}

/// <summary>
/// El chequeo de todo handler de Storage que carga un archivo por id (spec 2026-10-07): va
/// **después del 404** de otro tenant —que va primero para no confirmar que el id existe ahí— y
/// **antes de cualquier otra regla o efecto** (bucket, auditoría, <c>SaveChangesAsync</c>).
/// 403 <c>tenancy.module_not_enabled</c>. Con el tenant simulado por el stub no bloquea.
/// </summary>
public static class FileOwnerModuleGuard
{
    public static async Task EnsureOwnerModuleEnabledAsync(
        ITenantModules tenantModules,
        FileResource resource,
        CancellationToken cancellationToken)
    {
        if (FileOwnerModules.ModuleOf(resource.OwnerType) is { } module)
        {
            await TenantModuleGuard.EnsureEnabledAsync(tenantModules, resource.TenantId, module, cancellationToken);
        }
    }
}
