using BuildingBlocks.Application;
using Modules.Pricing.Domain;
using Modules.Tenancy.Application;

namespace Modules.Pricing.Application;

/// <summary>
/// Las cinco listas de precio con las que todo tenant arranca. Es la única lista fija del
/// dominio — el resto de <see cref="PriceList"/> lo administra cada tenant desde el CRUD.
/// </summary>
public static class DefaultPriceLists
{
    public static readonly IReadOnlyList<(string Prefix, string Name)> Definitions =
    [
        ("MIN", "Minorista"),
        ("MAY", "Mayorista"),
        ("DIS", "Distribuidor"),
        ("INS", "Institucional"),
        ("VIP", "VIP"),
    ];
}

/// <summary>
/// Siembra las cinco <see cref="DefaultPriceLists"/> en cada tenant al arrancar la app —
/// llamado desde <c>PricingDatabaseInitializer</c>, después de aplicar las migraciones.
///
/// Idempotente por <c>Prefix</c>: correrlo de nuevo sobre un tenant que ya tiene sus cinco
/// listas no agrega nada, mismo criterio que <c>GeographySeeder</c> (upsert por código DIVIPOLA)
/// pero acá la clave es <c>(tenantId, prefix)</c> en vez de un código global, porque
/// <see cref="PriceList"/> es tenant-scoped y no hay ningún tenant "de referencia" del que
/// copiar. Nunca renombra una lista existente ni la reactiva: si el tenant la editó o la
/// desactivó, esta corrida no la toca — sólo agrega la que falte.
///
/// Referencia <c>Modules.Tenancy.Application</c> para listar los tenants existentes — la única
/// dependencia de negocio que <c>PricingLayerTests</c> permite, la misma que usa cualquier otro
/// módulo para <c>IExecutionContext</c>/<c>IClock</c>.
/// </summary>
public sealed class DefaultPriceListsSeeder(
    ITenantRepository tenantRepository,
    IPriceListRepository priceListRepository,
    IPricingUnitOfWork unitOfWork,
    IClock clock)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var tenantIds = await tenantRepository.ListAllIdsAsync(cancellationToken);
        foreach (var tenantId in tenantIds)
        {
            await SeedForTenantAsync(tenantId.Value, cancellationToken);
        }
    }

    private async Task SeedForTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var existing = await priceListRepository.ListAsync(tenantId, cancellationToken);
        var existingPrefixes = existing
            .Select(priceList => priceList.Prefix)
            .ToHashSet(StringComparer.Ordinal);

        var now = clock.UtcNow;
        var added = false;
        foreach (var (prefix, name) in DefaultPriceLists.Definitions)
        {
            if (existingPrefixes.Contains(prefix))
            {
                continue;
            }

            priceListRepository.Add(PriceList.Create(PriceListId.New(), tenantId, name, prefix, now));
            added = true;
        }

        if (added)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
