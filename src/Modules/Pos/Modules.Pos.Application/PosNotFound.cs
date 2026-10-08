using BuildingBlocks.Application;

namespace Modules.Pos.Application;

/// <summary>404 dentro del tenant de la ruta (spec, decisión 29): los repositorios filtran por tenant.</summary>
internal static class PosNotFound
{
    public static ResourceNotFoundException Session(Guid id) =>
        new("pos.session.not_found", $"Cash session '{id}' was not found.");

    public static ResourceNotFoundException Sale(Guid id) =>
        new("pos.sale.not_found", $"Sale '{id}' was not found.");

    public static ResourceNotFoundException ProductCode(string code) =>
        new("pos.product.not_found", $"No product has the code '{code}'.");
}
