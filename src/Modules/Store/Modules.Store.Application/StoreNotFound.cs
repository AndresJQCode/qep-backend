using BuildingBlocks.Application;
using Modules.Store.Domain;

namespace Modules.Store.Application;

// Las búsquedas siempre están acotadas al tenant del llamador, así que «no encontrado» significa «no está entre tus solicitudes».
// Una de otro tenant no llega hasta acá: la autorización lo frena antes con 403.
internal static class StoreNotFound
{
    public static ResourceNotFoundException OrderRequest(Guid requestId) =>
        new(StoreErrorCodes.OrderRequestNotFound, $"Store order request '{requestId}' was not found.");
}
