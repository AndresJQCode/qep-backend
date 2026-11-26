namespace Modules.Wms.Domain.Inventory;

/// <summary>Quién escribió el movimiento. Espejo de <c>WmsActorType</c> (Application): el dominio no ve Application, y
/// <c>LedgerActorTypeMirrorsWmsActorType</c> falla si los nombres se separan.</summary>
public enum LedgerActorType
{
    Human,
    System,
    ApiKey,
}
