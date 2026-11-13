namespace Modules.Catalog.Application;

/// <summary>
/// La consola de la unidad tributaria del operador (spec 2026-11-10 «Impuestos por tipo»). Núcleo
/// (<c>RequiredModules: []</c>), categoría <c>Operator</c>, igual que <c>GeographyOperatorPermissions</c>: el prefijo
/// <c>operator.</c> hace que <c>OperatorPermissionFilter</c> los deje vivos sólo en el tenant operador, y la segunda capa de cada
/// handler es <c>OperatorAuthorization.EnsureAuthorized</c>.
/// </summary>
public static class CatalogOperatorPermissions
{
    /// <summary>Ver los valores cargados de la unidad tributaria de cada país.</summary>
    public const string TaxUnitsRead = "operator.tax_units.read";

    /// <summary>Cargar o corregir el valor de un año: cambia el umbral de las retenciones de todos los tenants.</summary>
    public const string TaxUnitsManage = "operator.tax_units.manage";
}
