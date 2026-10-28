using BuildingBlocks.Application;

namespace Modules.Quotations.Application;

// La búsqueda siempre está acotada al tenant de la ruta, que la autorización ya comprobó: "no
// encontrada" significa "no encontrada en tu tenant". Una transportadora de otro tenant da lo mismo
// que una inexistente, así que el 404 no confirma nada. Mismo criterio que QuotationNotFound.
internal static class CarrierNotFound
{
    public static ResourceNotFoundException For(Guid carrierId) =>
        new("quotations.carrier.not_found", $"Carrier '{carrierId}' was not found.");
}
