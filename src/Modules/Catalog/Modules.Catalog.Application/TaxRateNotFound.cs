using BuildingBlocks.Application;

namespace Modules.Catalog.Application;

// La búsqueda siempre está acotada al tenant del llamador, así que "no encontrado" acá significa
// "no encontrado en tu catálogo". Una tasa de otro tenant es inalcanzable antes, en la
// autorización, y responde 403 — nunca 404, que confirmaría que el id existe.
internal static class TaxRateNotFound
{
    public static ResourceNotFoundException For(Guid taxRateId) =>
        new("catalog.tax_rate.not_found", $"Tax rate '{taxRateId}' was not found.");
}
