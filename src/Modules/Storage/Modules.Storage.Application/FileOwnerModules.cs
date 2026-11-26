using System.Collections.Frozen;
using BuildingBlocks.Application;
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
            [FileOwnerType.Store] = TenantModuleKeys.Store,
            [FileOwnerType.SupportTicket] = TenantModuleKeys.Support,
            [FileOwnerType.MessageTemplate] = TenantModuleKeys.Messaging,
            [FileOwnerType.ChatbotMedia] = TenantModuleKeys.Automations,
            [FileOwnerType.HrDocument] = TenantModuleKeys.Hr,
            [FileOwnerType.WmsEvidence] = TenantModuleKeys.Wms,
            [FileOwnerType.WmsExport] = TenantModuleKeys.Wms,
        }.ToFrozenDictionary();

    public static TenantModuleKey? ModuleOf(FileOwnerType ownerType) => ByOwnerType[ownerType];

    /// <summary>
    /// El permiso que, además del <c>storage.file.*</c> de cada ruta, exige tocar un archivo de ese dueño: listarlo y cualquier ruta por
    /// id. <c>storage.file.read</c> es núcleo y lo tiene cualquier miembro del tenant, así que un archivo cuyo contenido es de otro
    /// módulo (el export de saldos de WMS) necesita el permiso de ese módulo. Es una tabla dispersa: un tipo ausente no pide nada más. Los permisos van como
    /// literal porque Storage no referencia a ningún módulo de negocio (misma razón por la que <see cref="ByOwnerType"/> habla de
    /// <c>TenantModuleKeys</c> y no de los módulos); una prueba amarra el literal a <c>WmsPermissions.StockExport</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<FileOwnerType, string> RequiredPermissionByOwnerType =
        new Dictionary<FileOwnerType, string>
        {
            [FileOwnerType.WmsExport] = "wms.stock.export",
        }.ToFrozenDictionary();

    public static string? RequiredPermissionOf(FileOwnerType ownerType) =>
        RequiredPermissionByOwnerType.TryGetValue(ownerType, out var permission) ? permission : null;

    /// <summary>
    /// Si otro módulo puede referenciar por id un archivo de ese dueño (foto de producto, comprobante de un pedido). Ni los de dueño de
    /// sistema ni los que piden un permiso extra: el adaptador no ve los permisos del archivo, y referenciarlo lo sacaría de debajo de
    /// ellos. Los adaptadores de Bootstrapper tratan al que no, como a un id que no existe.
    /// </summary>
    public static bool IsReferenceableByOtherModules(FileOwnerType ownerType) =>
        !SystemOwnedFiles.Includes(ownerType) && RequiredPermissionOf(ownerType) is null;

    /// <summary>Los tipos de dueño cuyo permiso extra le falta al llamador: el listado los excluye en SQL.</summary>
    public static IReadOnlyCollection<FileOwnerType> OwnerTypesLackingPermission(Func<string, bool> hasPermission) =>
        RequiredPermissionByOwnerType
            .Where(pair => !hasPermission(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();

    /// <summary>Los tipos de dueño cuyo módulo no es efectivo: el listado los excluye en SQL.</summary>
    public static IReadOnlyCollection<FileOwnerType> OwnerTypesDisabledIn(TenantModuleSet modules) =>
        ByOwnerType
            .Where(pair => pair.Value is { } key && !modules.IsEnabled(key))
            .Select(pair => pair.Key)
            .ToArray();
}

/// <summary>
/// El permiso extra que pide el dueño del archivo (<see cref="FileOwnerModules.RequiredPermissionByOwnerType"/>). Lo llama todo handler
/// de Storage que carga un archivo por id, justo después del guard de módulo y antes de cualquier otra regla o efecto. Mismo 403
/// <c>authorization.denied</c> que el resto de Storage.
/// </summary>
public static class FileOwnerPermissionGuard
{
    public static void EnsureCallerMayAccess(IExecutionContext executionContext, FileResource resource)
    {
        if (FileOwnerModules.RequiredPermissionOf(resource.OwnerType) is { } permission
            && !executionContext.HasPermission(permission))
        {
            throw new RequestForbiddenException(
                "authorization.denied",
                "The subject cannot perform this storage operation for this tenant.");
        }
    }
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
