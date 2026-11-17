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
    Product = 4,

    // Spec 2026-09-16, D2: un comprobante de pago de un pedido. Espera en staging/ hasta que se
    // adjunta (D4) y ahí se mueve al bucket público (D3). Los comprobantes subidos antes de v2
    // siguen siendo User y no cambian (D13).
    PaymentProof = 5,

    // Spec 2026-09-19: el logo de un tenant. Sube por el mismo pipeline que cualquier imagen
    // (POST /files, PUT prefirmado, complete) y Tenancy lo asigna después con su propio endpoint
    // — ver ITenantLogoStorage. OwnerId es el tenantId.
    Tenant = 6,

    // Spec de la tienda en línea, 2026-11-03: el banner de la tienda pública. Sube por el mismo pipeline (POST /files, PUT prefirmado,
    // complete) y se publica con PUT /files/{fileId}/publication; OwnerId es el tenantId. Se persiste por nombre en character
    // varying(20), así que no hay migración.
    Store = 7,

    // Soporte fase 3 (plan F30): un adjunto del formulario público de Soporte. Lo sube un visitante anónimo por IStorageSystemUploads
    // —nunca por POST /files— y OwnerId es un secreto aleatorio (el uploadKey) que sólo conoce quien abrió la sesión; el vínculo con el
    // ticket vive en support.ticket_attachments. Se persiste por nombre, así que no hay migración.
    SupportTicket = 8,

    // Spec 2026-11-17: la imagen fija del encabezado de una plantilla de WhatsApp. OwnerId es el tenantId. Sube sólo por la sesión de
    // Mensajería (IMessagingTemplateImageStore, con messaging.template.manage), nunca por POST /files: por eso es dueño de sistema.
    // Se persiste por nombre en character varying(20), así que no hay migración.
    MessageTemplate = 9
}

/// <summary>
/// Los dueños de sistema: archivos que sólo llegan por el camino del módulo que los creó, nunca por la consola de Storage (fix round 1
/// del Bloque D, Soporte fase 3). Para un miembro, con cualquier permiso <c>storage.file.*</c>, no existen: el listado los deja fuera,
/// cada ruta por id responde el mismo 404 que a un id de otro tenant (<c>SystemOwnedFileGuard</c>), <c>POST /files</c> no los crea y los
/// adaptadores de otros módulos (Catalog, Quotations) no los ven. Así no se filtra su <c>OwnerId</c>, que en un adjunto de Soporte es el
/// <c>uploadKey</c>. Un dueño de sistema nuevo entra acá y queda tapiado igual.
/// </summary>
public static class SystemOwnedFiles
{
    public static IReadOnlySet<FileOwnerType> OwnerTypes { get; } =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(new[] { FileOwnerType.SupportTicket, FileOwnerType.MessageTemplate });

    public static bool Includes(FileOwnerType ownerType) => OwnerTypes.Contains(ownerType);
}
