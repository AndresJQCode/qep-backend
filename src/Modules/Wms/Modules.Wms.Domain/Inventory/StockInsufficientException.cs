using BuildingBlocks.Domain;

namespace Modules.Wms.Domain.Inventory;

/// <summary>El origen no tiene saldo para el movimiento. Lleva la clave y lo disponible para que la pantalla diga cuánto falta
/// sin otra consulta.</summary>
public sealed class StockInsufficientException(string locationCode, Guid variantId, string? lotCode, decimal available)
    : DomainException(WmsErrorCodes.StockInsufficient, "No hay suficiente saldo en la ubicación de origen."),
      IHasProblemExtensions
{
    public IReadOnlyDictionary<string, object?> ProblemExtensions { get; } = new Dictionary<string, object?>
    {
        ["locationCode"] = locationCode,
        ["variantId"] = variantId,
        ["lotCode"] = lotCode,
        ["available"] = available,
    };
}
