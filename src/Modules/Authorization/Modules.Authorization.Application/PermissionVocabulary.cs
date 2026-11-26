namespace Modules.Authorization.Application;

/// <summary>
/// Los nombres en pantalla de las acciones y de los módulos del catálogo de permisos (spec 2026-11-16).
/// Los publica el backend para que el front no traduzca claves con un diccionario propio: una clave
/// nueva sin nombre la frena <c>CompositionRootTests.EveryActionAndCategoryHasALabel</c>.
/// </summary>
public static class PermissionVocabulary
{
    /// <summary>
    /// Las acciones que son columna de la matriz, en orden. Las demás van a «Otros» de su fila: son
    /// operaciones con su propio riesgo (anular, fusionar, operar la caja) que no se conceden de un clic
    /// junto con «Gestionar».
    /// </summary>
    public static IReadOnlyList<string> ColumnActions { get; } = ["read", "manage", "delete", "import"];

    public static IReadOnlyDictionary<string, string> ActionLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["read"] = "Ver",
        ["manage"] = "Gestionar",
        ["delete"] = "Borrar",
        ["import"] = "Importar",
        ["invite"] = "Invitar",
        ["upload"] = "Subir",
        ["publish"] = "Publicar",
        ["merge"] = "Fusionar",
        ["revoke"] = "Revocar",
        ["moderate"] = "Moderar",
        ["mark_paid"] = "Marcar como pagado",
        ["void"] = "Anular",
        ["invoice"] = "Facturar",
        ["purge"] = "Purgar",
        ["create"] = "Crear",
        ["discount"] = "Dar descuentos",
        ["customer"] = "Elegir cliente",
        ["operate"] = "Abrir y cerrar",
        // Spec 2026-11-17 (copiloto): usar el chat no es «Ver» —no da acceso a ningún dato— y gasta saldo del tenant.
        ["use"] = "Usar",
        // Spec 2026-11-21 (nómina): aprobar una quincena liquidada no es «Gestionar».
        ["approve"] = "Aprobar",
        // Spec WMS 2026-10-31 §11.5 (bodegas): operaciones de inventario con riesgo propio; ninguna es columna de la matriz.
        ["export"] = "Exportar",
        ["propose"] = "Proponer",
        ["dispatch"] = "Despachar",
        ["perform"] = "Registrar",
        ["resolve"] = "Resolver",
    };

    /// <summary>
    /// Nombre del módulo por <c>PermissionDefinition.Category</c>. Algunas categorías nacieron ya en
    /// español («Préstamos», «Soporte»); igual llevan su entrada para que la prueba no tenga excepciones.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CategoryLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Tenancy"] = "Organización",
        ["Storage"] = "Archivos",
        ["Catalog"] = "Catálogo",
        ["Companies"] = "Empresas",
        ["Customers"] = "Clientes",
        ["Suppliers"] = "Proveedores",
        ["Quotations"] = "Cotizaciones y pedidos",
        ["Reporting"] = "Reportes",
        ["Pos"] = "Punto de venta",
        ["Developer"] = "Desarrolladores",
        ["Platform"] = "Plataforma",
        ["Operator"] = "Operador de plataforma",
        ["Integrations"] = "Integraciones",
        ["Messaging"] = "Mensajería",
        ["Pipeline"] = "Embudo",
        ["Automations"] = "Automatizaciones",
        ["Ai"] = "Agentes IA",
        ["Contratos de servicio"] = "Contratos de servicio",
        ["Préstamos"] = "Préstamos",
        ["Soporte"] = "Soporte",
        ["Compras"] = "Compras",
        ["Mayorista"] = "Mayorista",
        ["Siigo"] = "Siigo",
        ["Recursos humanos"] = "Recursos humanos",
        ["Nómina"] = "Nómina",
        ["Agenda"] = "Agenda",
        ["Tienda"] = "Tienda",
        ["Bodegas"] = "Bodegas",
        ["uncategorized"] = "Sin categoría",
    };

    /// <summary>Sin entrada devuelve la clave: mejor una clave cruda en pantalla que un hueco.</summary>
    public static string LabelOfAction(string action) => ActionLabels.GetValueOrDefault(action, action);

    /// <summary>Sin entrada devuelve la clave: mejor una clave cruda en pantalla que un hueco.</summary>
    public static string LabelOfCategory(string category) => CategoryLabels.GetValueOrDefault(category, category);

    /// <summary>
    /// Fila y columna del permiso en la matriz. Sin <c>Shape</c> (el fallback de metadata de
    /// <see cref="RoleCatalog"/>) va a «Otros» de una fila propia, con su nombre como entidad y como
    /// acción: un permiso sin declarar no rompe el catálogo ni se cuela en una columna que no es suya.
    /// </summary>
    public static PermissionPlacement PlacementOf(PermissionDefinition permission) =>
        permission.Shape is { } shape
            ? new PermissionPlacement(shape.Entity, shape.EntityLabel, shape.Action, LabelOfAction(shape.Action))
            : new PermissionPlacement(permission.Permission, permission.DisplayName, OtherAction, permission.DisplayName);

    /// <summary>La acción de un permiso sin <c>Shape</c>. No es columna: cae en «Otros».</summary>
    public const string OtherAction = "other";
}

/// <summary>Dónde va un permiso en la matriz de roles y con qué nombres en pantalla.</summary>
public sealed record PermissionPlacement(string Entity, string EntityLabel, string Action, string ActionLabel);
