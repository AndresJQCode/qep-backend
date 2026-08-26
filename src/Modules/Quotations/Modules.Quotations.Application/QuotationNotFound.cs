using BuildingBlocks.Application;

namespace Modules.Quotations.Application;

// La busqueda siempre esta acotada al tenant del llamador: "no encontrada" acá significa "no
// encontrada en tu tenant". Una cotización de otro tenant es inalcanzable antes, en la
// autorización, y responde 403 — nunca 404, que confirmaría que el id existe.
internal static class QuotationNotFound
{
    public static ResourceNotFoundException For(Guid quotationId) =>
        new("quotation.quotation.not_found", $"Quotation '{quotationId}' was not found.");
}
