namespace Modules.ApiAccess.Application;

public static class ApiAccessPermissions
{
    /// <summary>
    /// Crear, ver y revocar las claves de API del tenant. De fábrica solo en <c>admin</c>: una clave
    /// actúa en nombre de la empresa sin que nadie esté conectado.
    /// </summary>
    public const string ApiKeyManage = "developer.api_key.manage";
}
