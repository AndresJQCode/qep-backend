using Modules.Geography.Application;
using Modules.Geography.Domain;
using Modules.Wms.Application;

namespace Bootstrapper;

/// <summary>Geography → Wms (puerto <see cref="IWmsGeographyReader"/>): sólo el nombre de la ciudad de una bodega. Mismo patrón de
/// <c>SupplierGeographyLookup</c>: Wms no referencia Geography y el composition root cablea el puerto.</summary>
internal sealed class WmsGeographyReader(ICityRepository cities) : IWmsGeographyReader
{
    public async Task<IReadOnlyDictionary<Guid, WmsCityRef>> FindCitiesAsync(IReadOnlyCollection<Guid> cityIds, CancellationToken ct)
    {
        if (cityIds.Count == 0)
        {
            return new Dictionary<Guid, WmsCityRef>();
        }

        // ListByIdsAsync recibe una colección, no un IEnumerable: el Select se materializa.
        var found = await cities.ListByIdsAsync(cityIds.Distinct().Select(id => new CityId(id)).ToArray(), ct);
        return found.ToDictionary(city => city.Id.Value, city => new WmsCityRef(city.Id.Value, city.Name));
    }
}
