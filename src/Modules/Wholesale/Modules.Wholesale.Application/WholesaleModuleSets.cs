using Modules.Tenancy.Domain;

namespace Modules.Wholesale.Application;

/// <summary>
/// D-21: reglas de Mayorista sobre claves de Tenancy. Cada conjunto viene cerrado bajo sus dependencias, porque Tenancy rechaza
/// un lote que deja un módulo sin ellas (<c>WholesaleModuleSetsTests</c>).
/// </summary>
public static class WholesaleModuleSets
{
    /// <summary>Con lo que nace el tenant de un distribuidor que acepta creando uno (decisión del owner, 2026-11-23): sólo
    /// catálogo, proveedores y compras, lo mínimo para recibir del mayorista. El resto se prende por tenant.</summary>
    public static readonly IReadOnlyList<TenantModuleKey> InvitedDistributor =
    [
        TenantModuleKeys.Catalog, TenantModuleKeys.Suppliers, TenantModuleKeys.Purchases,
    ];

    /// <summary>D-5: lo que un tenant existente necesita para vincularse. <c>catalog</c> va porque <c>purchases</c> depende de él.</summary>
    public static readonly IReadOnlyList<TenantModuleKey> LinkedDistributorRequired =
    [
        TenantModuleKeys.Catalog, TenantModuleKeys.Suppliers, TenantModuleKeys.Purchases,
    ];
}
