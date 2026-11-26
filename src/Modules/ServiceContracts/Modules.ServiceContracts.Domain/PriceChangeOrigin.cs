namespace Modules.ServiceContracts.Domain;

/// <summary>De dónde salió una fila del historial de precios (spec reajuste §6.12): una edición o un cambio programado.</summary>
public enum PriceChangeOrigin
{
    Edit = 1,
    Scheduled = 2,
    Ipc = 3,
}
