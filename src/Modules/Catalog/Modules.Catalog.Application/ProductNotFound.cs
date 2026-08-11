using BuildingBlocks.Application;

namespace Modules.Catalog.Application;

// La búsqueda siempre está acotada al tenant del llamador, así que "no encontrado" acá
// significa "no encontrado en tu catálogo". Un producto de otro tenant es inalcanzable antes,
// en la autorización, y responde 403 — nunca 404, que confirmaría que el id existe.
internal static class ProductNotFound
{
    public static ResourceNotFoundException For(Guid productId) =>
        new("catalog.product.not_found", $"Product '{productId}' was not found.");
}
