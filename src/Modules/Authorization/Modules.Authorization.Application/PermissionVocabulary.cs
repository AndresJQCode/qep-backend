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
        ["Tienda"] = "Tienda",
        ["uncategorized"] = "Sin categoría",
    };

    /// <summary>Sin entrada devuelve la clave: mejor una clave cruda en pantalla que un hueco.</summary>
    public static string LabelOfAction(string action) => ActionLabels.GetValueOrDefault(action, action);

    /// <summary>Sin entrada devuelve la clave: mejor una clave cruda en pantalla que un hueco.</summary>
    public static string LabelOfCategory(string category) => CategoryLabels.GetValueOrDefault(category, category);
}
