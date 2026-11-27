using BuildingBlocks.Application;
using Modules.Geography.Application;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Invoicing;

namespace Bootstrapper;

/// <summary>
/// El adaptador de <see cref="ISiigoForeignCityInbox"/> sobre <see cref="IForeignCityReporter"/> de Geography (spec 2026-11-26). Escribe:
/// es la otra mitad de la resolución que hace <see cref="SiigoGeographyLookup"/>, en otra clase a propósito. Sólo la vista previa y la
/// emisión de Siigo lo llaman; la disponibilidad no lo recibe. Vive en Bootstrapper porque Siigo no referencia a Geography.
/// </summary>
internal sealed class SiigoForeignCityInbox(IForeignCityReporter reporter, IClock clock) : ISiigoForeignCityInbox
{
    public async Task ReportAsync(Guid tenantId, Guid orderId, SiigoUnlinkedCity city, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(city);
        await reporter.ReportAsync(
            new ForeignCityReport(tenantId, orderId, city.CountryCode, city.CityText, clock.UtcNow), cancellationToken);
    }
}
