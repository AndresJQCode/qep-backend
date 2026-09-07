using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Notifications.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart) para que una sección Notifications mal
// configurada se detecte en el boot y no en el primer email. Las credenciales de Infobip
// sólo se exigen cuando ese proveedor está seleccionado.
internal sealed class NotificationsOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<NotificationsOptions>
{
    private static readonly string[] KnownProviders =
        [NotificationsOptions.LogProvider, NotificationsOptions.InfobipProvider];

    public ValidateOptionsResult Validate(string? name, NotificationsOptions options)
    {
        var failures = new List<string>();

        // No alcanza con UriKind.Absolute: en Linux, "/invitations" es una ruta de archivo
        // absoluta valida y Uri.TryCreate la acepta como file:///invitations. El esquema
        // tiene que ser http o https, o una URL de sistema de archivos pasa la validacion.
        if (string.IsNullOrWhiteSpace(options.InvitationUrl)
            || !Uri.TryCreate(options.InvitationUrl, UriKind.Absolute, out var invitationUrl)
            || (invitationUrl.Scheme != Uri.UriSchemeHttp
                && invitationUrl.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Notifications:InvitationUrl must be an absolute URL.");
        }

        if (!KnownProviders.Contains(options.EmailProvider, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Notifications:EmailProvider '{options.EmailProvider}' is not supported "
                + "(expected 'log' or 'infobip').");
        }

        // El canal "log" registra el correo y no lo manda. Fuera de producción ese no-op es el
        // que deja levantar la API y correr las pruebas de integración sin aprovisionar Infobip.
        // En producción es una trampa silenciosa: la invitación se marca entregada, el worker no
        // registra ningún error y el destinatario nunca recibe nada — indistinguible de un envío
        // real. Mismo criterio que QuotationsOptionsValidator con Zenvia: se prefiere que el
        // arranque falle a que el negocio pierda altas sin enterarse.
        if (environment.IsProduction()
            && string.Equals(
                options.EmailProvider,
                NotificationsOptions.LogProvider,
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Notifications:EmailProvider cannot be '{NotificationsOptions.LogProvider}' in "
                + "Production: the invitation is marked as delivered but no email leaves the "
                + "system.");
        }

        if (string.Equals(
            options.EmailProvider,
            NotificationsOptions.InfobipProvider,
            StringComparison.OrdinalIgnoreCase))
        {
            var infobip = options.Infobip;
            if (string.IsNullOrWhiteSpace(infobip.BaseUrl))
            {
                failures.Add("Notifications:Infobip:BaseUrl is required when EmailProvider is 'infobip'.");
            }

            if (string.IsNullOrWhiteSpace(infobip.ApiKey))
            {
                failures.Add("Notifications:Infobip:ApiKey is required when EmailProvider is 'infobip'.");
            }

            if (string.IsNullOrWhiteSpace(infobip.SenderEmail))
            {
                failures.Add("Notifications:Infobip:SenderEmail is required when EmailProvider is 'infobip'.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
