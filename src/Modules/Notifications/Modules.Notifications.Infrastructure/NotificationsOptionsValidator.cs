using Microsoft.Extensions.Options;

namespace Modules.Notifications.Infrastructure;

// Falla rápido al arrancar (ValidateOnStart) para que una sección Notifications mal
// configurada se detecte en el boot y no en el primer email. Las credenciales de Infobip
// sólo se exigen cuando ese proveedor está seleccionado.
internal sealed class NotificationsOptionsValidator : IValidateOptions<NotificationsOptions>
{
    private static readonly string[] KnownProviders =
        [NotificationsOptions.LogProvider, NotificationsOptions.InfobipProvider];

    public ValidateOptionsResult Validate(string? name, NotificationsOptions options)
    {
        var failures = new List<string>();

        // No alcanza con UriKind.Absolute: en Linux, "/login" es una ruta de archivo
        // absoluta valida y Uri.TryCreate la acepta como file:///login. El esquema tiene
        // que ser http o https, o una URL de sistema de archivos pasa la validacion.
        if (string.IsNullOrWhiteSpace(options.LoginUrl)
            || !Uri.TryCreate(options.LoginUrl, UriKind.Absolute, out var loginUrl)
            || (loginUrl.Scheme != Uri.UriSchemeHttp && loginUrl.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add("Notifications:LoginUrl must be an absolute URL.");
        }

        if (!KnownProviders.Contains(options.EmailProvider, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Notifications:EmailProvider '{options.EmailProvider}' is not supported "
                + "(expected 'log' or 'infobip').");
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
