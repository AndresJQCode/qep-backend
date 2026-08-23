using BuildingBlocks.Application;

namespace Modules.Pricing.Application;

// La busqueda siempre esta acotada al tenant del llamador, asi que "no encontrada" aca significa
// "no encontrada entre las listas de precio de tu tenant". Una lista de otro tenant es
// inalcanzable antes, en la autorizacion, y responde 403 — nunca 404, que confirmaria que el id
// existe.
internal static class PriceListNotFound
{
    public static ResourceNotFoundException For(Guid priceListId) =>
        new(
            "pricing.price_list.not_found",
            $"Price list '{priceListId}' was not found.");
}
