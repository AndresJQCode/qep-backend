using BuildingBlocks.Application;

namespace Modules.Customers.Application;

// La busqueda siempre esta acotada al tenant del llamador, asi que "no encontrado" aca significa
// "no encontrado entre tus clientes". Un cliente de otro tenant es inalcanzable antes, en la
// autorizacion, y responde 403 — nunca 404, que confirmaria que el id existe.
internal static class CustomerNotFound
{
    public static ResourceNotFoundException For(Guid customerId) =>
        new("customers.customer.not_found", $"Customer '{customerId}' was not found.");
}
