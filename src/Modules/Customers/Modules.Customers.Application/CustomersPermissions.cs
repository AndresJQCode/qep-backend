namespace Modules.Customers.Application;

// Tres segmentos, module.resource.action, igual que todos los permisos ya registrados
// (TenancyPermissions, StoragePermissions, CatalogPermissions, CompaniesPermissions).
//
// Los tres los declara `CLI-01`. `customers.import` es propio y no parte de `manage` porque cargar
// un Excel de mil clientes de una vez no es la misma autoridad que editar uno: el gate del modulo
// pide explicitamente que se mapeen a roles por separado.
public static class CustomersPermissions
{
    public const string CustomerRead = "customers.customer.read";

    public const string CustomerManage = "customers.customer.manage";

    public const string CustomerImport = "customers.customer.import";

    // El catalogo de clasificaciones de cliente (nombre + prefijo) vive en el mismo modulo que
    // Customer, pero es un recurso distinto con sus propios permisos, mismo criterio que
    // CatalogPermissions.TaxRateRead/TaxRateManage frente a ProductRead/ProductManage.
    public const string ClassificationRead = "customers.classification.read";

    public const string ClassificationManage = "customers.classification.manage";

    // Spec 2026-10-11 §4.3. Crear una etiqueta en línea es CustomerManage (quien etiqueta crea, como en
    // respond.io); renombrar, recolorear y borrar cambia lo que ven todos y vacía campañas, así que es de admin.
    public const string TagManage = "customers.tag.manage";

    // Borrar notas ajenas. El autor borra las suyas con CustomerManage.
    public const string NoteModerate = "customers.note.moderate";

    // Spec 2026-10-24 D-F1. Fusionar varios clientes en uno no se puede deshacer, así que es un permiso aparte de
    // CustomerManage: un rol custom puede tener uno y no el otro.
    public const string CustomerMerge = "customers.customer.merge";
}

/// <summary>
/// Políticas que no son un permiso: combinan varios. La de borrar una nota deja pasar a quien pueda hacerlo por
/// alguna de las dos vías —el autor con <see cref="CustomersPermissions.CustomerManage"/> o quien tiene
/// <see cref="CustomersPermissions.NoteModerate"/>—; el handler decide cuál aplica según de quién es la nota.
/// Se registra en <c>AddAuthorization</c> junto a los permisos; sin ella <c>RequireAuthorization</c> no resuelve y el
/// síntoma es un 500.
/// </summary>
public static class CustomersPolicies
{
    public const string NoteDelete = "customers.note.delete";
}
