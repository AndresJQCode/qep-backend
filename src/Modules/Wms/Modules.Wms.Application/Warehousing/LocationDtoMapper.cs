using System.Security.Cryptography;
using System.Text;
using Modules.Wms.Domain.Warehousing;

namespace Modules.Wms.Application;

internal static class LocationDtoMapper
{
    public static LocationDto ToDto(Location location)
    {
        ArgumentNullException.ThrowIfNull(location);

        return new LocationDto(
            location.Id.Value,
            location.WarehouseId.Value,
            location.ParentId?.Value,
            location.Code,
            location.Path,
            location.Kind.ToString(),
            location.Role.ToString(),
            location.IsVirtual,
            location.AllowsStorage,
            location.AllowsPicking,
            location.Capacity.MaxWeightKg,
            location.Capacity.MaxVolumeM3,
            location.Capacity.MaxUnits,
            location.PutawayPriority,
            location.StorageClasses.ToList(),
            location.Status.ToString(),
            location.Version);
    }

    /// <summary>Plano y ordenado por <c>Path</c> ordinal: la app arma el árbol y el orden no depende de la cultura del servidor.</summary>
    public static LocationsDto ToDto(IEnumerable<Location> locations) =>
        new(locations.OrderBy(location => location.Path, StringComparer.Ordinal).Select(ToDto).ToList());

    public static ScannableLocationsDto ToScannable(IEnumerable<Location> locations)
    {
        // Las virtuales no se escanean (no tienen etiqueta) y nunca cambian por PUT, así que no entran ni en la lista ni en el etag.
        var scannable = locations.Where(location => !location.IsVirtual).ToList();

        return new ScannableLocationsDto(
            scannable
                .OrderBy(location => location.Path, StringComparer.Ordinal)
                .Select(location => new ScannableLocationDto(location.Id.Value, location.Code, location.Path, location.Role.ToString()))
                .ToList(),
            Etag(scannable));
    }

    /// <summary>
    /// Cambia si alguna ubicación no virtual se crea, se borra o se edita (cada edición sube su <c>Version</c>). Usa id y versión y
    /// no el contenido: es barato y la app sólo necesita saber «cambió algo». Mover un padre reescribe los paths de los hijos sin
    /// subirles la versión, pero eso sube la del nodo movido, que también entra.
    /// </summary>
    internal static string Etag(IEnumerable<Location> nonVirtual)
    {
        var material = string.Join(
            ';',
            nonVirtual.OrderBy(location => location.Id.Value).Select(location => $"{location.Id.Value:N}:{location.Version}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));

        return "\"" + Convert.ToHexStringLower(hash.AsSpan(0, 8)) + "\"";
    }
}
