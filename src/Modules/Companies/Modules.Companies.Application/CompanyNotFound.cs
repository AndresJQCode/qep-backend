using BuildingBlocks.Application;

namespace Modules.Companies.Application;

// La busqueda siempre esta acotada al tenant del llamador, asi que "no encontrada" aca significa
// "no encontrada entre tus empresas". Una empresa de otro tenant es inalcanzable antes, en la
// autorizacion, y responde 403 — nunca 404, que confirmaria que el id existe.
internal static class CompanyNotFound
{
    public static ResourceNotFoundException For(Guid companyId) =>
        new("companies.company.not_found", $"Company '{companyId}' was not found.");
}
