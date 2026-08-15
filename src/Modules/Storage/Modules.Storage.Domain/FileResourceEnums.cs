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

// A qué le pertenece un archivo. **Se persiste por nombre**, no por número: StorageDbContext lo
// mapea con HasConversion<string>() sobre character varying(20), igual que FileResourceStatus.
// Lo que no se puede cambiar, entonces, es el **nombre** de un valor ya usado —renombrarlo deja
// las filas viejas ilegibles—; el número es interno y agregar valores es seguro.
public enum FileOwnerType
{
    User = 1,
    Entity = 2,
    System = 3,

    // CAT-05: un archivo puede pertenecer a un producto del catálogo. Antes quedaba guardado como
    // User, porque el endpoint caía en silencio a ese valor cuando el string no parseaba.
    Product = 4
}
