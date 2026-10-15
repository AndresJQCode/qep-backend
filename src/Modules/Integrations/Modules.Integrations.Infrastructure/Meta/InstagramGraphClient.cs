using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>
/// Spec 2026-10-14 §6.1: el cliente de Graph de Instagram (<c>integrations.instagram-graph</c>), con el patrón de
/// <see cref="MetaGraphClient"/>: sin redirecciones (el token viaja en <c>Authorization</c>), 10 s, sin loggers de
/// headers. La base es <c>graph.instagram.com</c>; el canje del <c>code</c> va a <c>api.instagram.com</c> con una
/// URI absoluta sobre el mismo cliente. Registra status y códigos; nunca el token, el <c>code</c> ni el
/// <c>client_secret</c>.
/// </summary>
internal sealed class InstagramGraphClient(
    IHttpClientFactory httpClientFactory,
    IOptions<MetaInstagramOptions> options,
    ILogger<InstagramGraphClient> logger)
{
    public const string HttpClientName = "integrations.instagram-graph";

    public const string BaseUrl = "https://graph.instagram.com/";

    /// <summary>El canje del <c>code</c> vive en otro host que el resto de la API.</summary>
    public const string CodeExchangeUrl = "https://api.instagram.com/oauth/access_token";

    internal static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    internal static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("qep-integrations");
    }

    public string Version => string.IsNullOrWhiteSpace(options.Value.GraphApiVersion)
        ? MetaAppOptions.DefaultGraphApiVersion
        : options.Value.GraphApiVersion.Trim();

    /// <summary>Ruta relativa a la versión: <c>Path("me?fields=…")</c> → <c>v24.0/me?fields=…</c>.</summary>
    public string Path(string relative) => $"{Version}/{relative.TrimStart('/')}";

    public Task<MetaGraphResponse> SendAsync(
        string operation, HttpRequestMessage request, string? accessToken, CancellationToken cancellationToken) =>
        MetaGraphClient.SendWithAsync(httpClientFactory.CreateClient(HttpClientName), operation, request, accessToken, logger, cancellationToken);
}
