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
    /// una variable propia que el pipeline tenga que dar de alta. <paramref name="emailProof"/> va como <c>?ev=</c> sólo en el link
    /// del correo (spec 2026-11-24): le deja saber al backend que quien llega controla ese buzón. El link para «Copiar» y el de
    /// WhatsApp no la llevan.
    /// </summary>
    public static string WholesaleInvitation(string appBaseUrl, string token, string? emailProof = null)
    {
        var link = $"{Trim(appBaseUrl)}/wholesale-invitations/{Uri.EscapeDataString(token)}";
        return string.IsNullOrEmpty(emailProof) ? link : $"{link}?ev={Uri.EscapeDataString(emailProof)}";
    }

    /// <summary>The detail of a support ticket or task in the SPA, for the email of an escalated assignment.</summary>
    public static string SupportTask(string appBaseUrl, Guid ticketId) => $"{Trim(appBaseUrl)}/support/{ticketId:D}";

    private static string Trim(string appBaseUrl) => appBaseUrl.TrimEnd('/');
}
