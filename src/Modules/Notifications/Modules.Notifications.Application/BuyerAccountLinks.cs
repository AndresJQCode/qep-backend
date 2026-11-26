namespace Modules.Notifications.Application;

/// <summary>
/// Los enlaces de los correos del comprador. Apuntan al host de la tienda de origen, no al frontend de
/// los tenants. El token va escapado: es base64url, pero el enlace no debe depender de eso.
/// </summary>
public static class BuyerAccountLinks
{
    public static string Verify(string host, string token) =>
        $"https://{host}/cuenta/verificar?token={Uri.EscapeDataString(token)}";

    public static string Reset(string host, string token) =>
        $"https://{host}/cuenta/restablecer?token={Uri.EscapeDataString(token)}";

    public static string Login(string host) => $"https://{host}/cuenta/ingresar";

    public static string Forgot(string host) => $"https://{host}/cuenta/olvide";
}
