namespace Modules.Notifications.Application;

/// <summary>
/// Arma el deep-link de invitación que viaja en el email: la base configurada
/// (<c>Notifications:InvitationUrl</c>) más el token como segmento de path. Vive acá y no
/// en el worker para que el formato del link —el contrato con las rutas del frontend—
/// tenga una única definición y sea verificable sin levantar el host.
/// </summary>
public static class InvitationLink
{
    public static string Compose(string invitationUrl, string token) =>
        $"{invitationUrl.TrimEnd('/')}/{token}";
}
