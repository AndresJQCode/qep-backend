namespace Modules.Integrations.Application;

/// <summary>
/// Spec 2026-10-08, «Permisos». Núcleo (<c>RequiredModules</c> vacío, decisión 4): lo que se filtra por
/// módulo es el catálogo de proveedores, no el permiso. Cada uno necesita su política en
/// <c>AddAuthorization</c>: sin ella <c>RequireAuthorization</c> no resuelve y el síntoma es 500, no 403.
/// </summary>
public static class IntegrationsPermissions
{
    public const string ConnectionRead = "integrations.connection.read";
    public const string ConnectionManage = "integrations.connection.manage";
}
