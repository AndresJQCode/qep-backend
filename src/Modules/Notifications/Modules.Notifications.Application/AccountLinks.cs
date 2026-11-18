namespace Modules.Notifications.Application;

/// <summary>
/// Los enlaces de los correos de cuenta, armados contra <c>Notifications:AppBaseUrl</c> (la base del
/// frontend) y no contra <c>InvitationUrl</c>. El token va escapado: es base64url, pero el enlace no
/// debe depender de eso.
/// </summary>
public static class AccountLinks
{
    public static string VerifyEmail(string appBaseUrl, string token) =>
        $"{Trim(appBaseUrl)}/verify-email?token={Uri.EscapeDataString(token)}";

    public static string ResetPassword(string appBaseUrl, string token) =>
        $"{Trim(appBaseUrl)}/reset-password?token={Uri.EscapeDataString(token)}";

    public static string Login(string appBaseUrl) => $"{Trim(appBaseUrl)}/login";

    public static string ForgotPassword(string appBaseUrl) => $"{Trim(appBaseUrl)}/forgot-password";

    /// <summary>
    /// El link de la invitación de un mayorista (ruling W1): la pantalla <c>/wholesale-invitations/{token}</c> del frontend, sin
    /// una variable propia que el pipeline tenga que dar de alta.
    /// </summary>
    public static string WholesaleInvitation(string appBaseUrl, string token) =>
        $"{Trim(appBaseUrl)}/wholesale-invitations/{Uri.EscapeDataString(token)}";

    private static string Trim(string appBaseUrl) => appBaseUrl.TrimEnd('/');
}
