using BuildingBlocks.Domain.Pricing;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Tenancy.Infrastructure;

/// <summary>
/// Lee la regla de redondeo del tenant desde su tabla. Una moneda sin fila, o un tenant que no
/// existe, devuelve el valor por defecto del catálogo: la regla no necesita que el tenant exista.
/// </summary>
public sealed class TenantPriceRoundingReader(ITenantPriceRoundingRepository repository) : ITenantPriceRounding
{
    public async Task<PriceRounding> GetAsync(Guid tenantId, string currency, CancellationToken cancellationToken)
    {
        var code = Currencies.Normalize(currency);
        var rows = await repository.ListByTenantAsync(new TenantId(tenantId), cancellationToken);
        return rows.FirstOrDefault(row => row.Currency == code)?.Rule ?? PriceRoundingDefaults.For(code);
    }

    public async Task<IReadOnlyDictionary<string, PriceRounding>> GetAllAsync(
        Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = (await repository.ListByTenantAsync(new TenantId(tenantId), cancellationToken))
            .ToDictionary(row => row.Currency, StringComparer.Ordinal);
        return Currencies.All.ToDictionary(
            currency => currency.Code,
            currency => rows.TryGetValue(currency.Code, out var row) ? row.Rule : PriceRoundingDefaults.For(currency.Code),
            StringComparer.Ordinal);
    }
}
