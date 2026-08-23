using BuildingBlocks.Application;

namespace Modules.Customers.Application;

// La busqueda siempre esta acotada al tenant del llamador, asi que "no encontrado" aca significa
// "no encontrado entre las clasificaciones de tu tenant". Una clasificacion de otro tenant es
// inalcanzable antes, en la autorizacion, y responde 403 — nunca 404, que confirmaria que el id
// existe.
internal static class ClientClassificationNotFound
{
    public static ResourceNotFoundException For(Guid classificationId) =>
        new(
            "customers.classification.not_found",
            $"Client classification '{classificationId}' was not found.");
}
