namespace Modules.Hr.Application;

/// <summary>
/// Los permisos de operador de Hr (spec 2026-11-22, «Permisos»): viven en el tenant operador, no en el catálogo de los tenants, y los
/// handlers los validan con <c>OperatorAuthorization</c> sin pasar por el módulo <c>hr</c>. Cada permiso necesita su política en
/// <c>QepServiceCollectionExtensions</c>: sin ella <c>RequireAuthorization</c> no resuelve y el síntoma es un 500, no un 403.
/// </summary>
public static class HrOperatorPermissions
{
    public const string LegalParametersRead = "operator.legal_parameters.read";

    public const string LegalParametersManage = "operator.legal_parameters.manage";
}
