using Microsoft.Extensions.Options;
using Modules.Notifications.Application;

namespace Modules.Notifications.Infrastructure.Channels;

public sealed class InfobipOptions
{
    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string SenderEmail { get; init; } = string.Empty;
}

// Canal de email de producción sobre la API HTTP Email de Infobip (ADR 0018). Las
// credenciales son secretos por entorno ligados a NotificationsOptions. El endpoint y la
// forma exactos pueden necesitar alinearse con la versión actual de la API de Infobip; el
// canal de log es el default hasta que se aprovisionen las credenciales.
internal sealed class InfobipEmailChannel(
    HttpClient httpClient,
    IOptions<NotificationsOptions> options)
    : IEmailChannel
{
    private readonly InfobipOptions settings = options.Value.Infobip;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent
        {
            { new StringContent(settings.SenderEmail), "from" },
            { new StringContent(message.ToAddress), "to" },
            { new StringContent(message.Subject), "subject" },
            { new StringContent(message.TextBody), "text" },
            { new StringContent(message.HtmlBody), "html" },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{settings.BaseUrl.TrimEnd('/')}/email/3/send")
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"App {settings.ApiKey}");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
