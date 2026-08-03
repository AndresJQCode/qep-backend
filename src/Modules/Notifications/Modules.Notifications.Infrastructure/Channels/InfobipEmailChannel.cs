using Microsoft.Extensions.Options;
using Modules.Notifications.Application;

namespace Modules.Notifications.Infrastructure.Channels;

public sealed class InfobipOptions
{
    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string SenderEmail { get; init; } = string.Empty;
}

// Production email channel over the Infobip HTTP Email API (ADR 0018). Credentials
// are per-environment secrets bound into NotificationsOptions. The exact endpoint/
// shape may need alignment with the current Infobip API version; the log channel is
// the default until credentials are provisioned.
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
