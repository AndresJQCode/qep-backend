using Modules.Geography.Application;
using Modules.Shopify.Application;

namespace Bootstrapper;

/// <summary>Spec 2026-10-26 D-C12 y S6: Geography → Shopify. «Recorrer la historia previa» de la consola corre el recorrido del
/// espejo de Shopify (<see cref="ShopifyUnresolvedCityBackfill"/>), que reporta cada ciudad sin resolver a la bandeja por
/// <c>IShopifyUnresolvedCityReporter</c>. Geography no referencia Shopify: el puerto es de Geography y éste es el único lugar que
/// ve los dos.</summary>
internal sealed class HistoricalCityBackfillAdapter(ShopifyUnresolvedCityBackfill backfill) : IHistoricalCityBackfill
{
    public async Task<CityBackfillResultDto> RunAsync(CancellationToken cancellationToken)
    {
        var result = await backfill.RunAsync(cancellationToken);
        return new CityBackfillResultDto(result.Scanned, result.Reported, result.AlreadyReported, result.NowMatching, result.Skipped);
    }
}
