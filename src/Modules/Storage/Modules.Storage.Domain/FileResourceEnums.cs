namespace Modules.Storage.Domain;

// Ciclo de vida de subida de un recurso de archivo (contrato de capacidad). Sólo Available
// es descargable; un recurso borrado lógicamente se retiene antes del purgado físico.
public enum FileResourceStatus
{
    PendingUpload = 1,
    PendingScan = 2,
    Available = 3,
    Quarantined = 4,
    Deleted = 5,
    Purged = 6
}

public enum FileOwnerType
{
    User = 1,
    Entity = 2,
    System = 3
}
