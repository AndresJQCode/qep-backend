using BuildingBlocks.Application;
using Modules.Billing.Domain;

namespace Modules.Billing.Application;

// Las búsquedas siempre están acotadas al tenant del llamador, así que «no encontrado» significa «no está entre
// tus servicios/cobros». Uno de otro tenant no llega hasta acá: la autorización lo frena antes con 403.
internal static class BillingNotFound
{
    public static ResourceNotFoundException Service(Guid serviceId) =>
        new(BillingErrorCodes.ServiceNotFound, $"Billing service '{serviceId}' was not found.");

    public static ResourceNotFoundException Charge(Guid chargeId) =>
        new(BillingErrorCodes.ChargeNotFound, $"Charge '{chargeId}' was not found.");

    public static ResourceNotFoundException Profile(Guid customerId) =>
        new(BillingErrorCodes.ProfileNotFound, $"Billing profile of customer '{customerId}' was not found.");
}
