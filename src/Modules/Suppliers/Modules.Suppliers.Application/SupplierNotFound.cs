using BuildingBlocks.Application;

namespace Modules.Suppliers.Application;

// La búsqueda siempre está acotada al tenant del llamador, así que «no encontrado» aquí significa
// «no está entre tus proveedores». Uno de otro tenant no llega hasta acá: la autorización lo frena
// antes con 403, nunca 404, que confirmaría que el id existe.
internal static class SupplierNotFound
{
    public static ResourceNotFoundException For(Guid supplierId) =>
        new("suppliers.supplier.not_found", $"Supplier '{supplierId}' was not found.");
}
