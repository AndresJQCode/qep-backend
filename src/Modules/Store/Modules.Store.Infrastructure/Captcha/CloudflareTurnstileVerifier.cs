using System.Net;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Modules.Store.Application;
using Modules.Store.Domain;

namespace Modules.Store.Infrastructure.Captcha;

/// <summary>
/// Cloudflare Turnstile por <c>siteverify</c>. Un "no" de Cloudflare es <c>false</c>; un Cloudflare que no contesta (tiempo, red, 5xx) es
/// 503 <c>store.captcha.unavailable</c>, que no culpa al visitante. Sin llave configurada falla CERRADO y también como 503: es un error de
/// despliegue y tiene que verse, no pasar por un bot. Nunca registra la llave ni el token.
/// </summary>
public sealed partial class CloudflareTurnstileVerifier(
    IHttpClientFactory httpClientFactory,
    StoreOptions options,
    bool enforceHostname,
    ILogger<CloudflareTurnstileVerifier> logger) : ICaptchaVerifier
{
    public const string HttpClientName = "store.turnstile";

    /// <summary>Cinco segundos: el visitante está esperando y Cloudflare contesta en milisegundos.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turnstile is enabled but Store:Turnstile:SecretKey is not configured; every submission is refused.")]
    private static partial void LogNoKey(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turnstile siteverify could not be used: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Turnstile refused a token (success {Success}, hostname match {HostnameMatch}, codes {Codes})")]
    private static partial void LogRefused(ILogger logger, bool success, bool hostnameMatch, string codes);

    /// <summary>Sin redirecciones: el secreto viaja en el cuerpo y un 3xx no debe llevarlo a otro host.</summary>
    public static SocketsHttpHandler CreatePrimaryHandler() => new() { AllowAutoRedirect = false };

    public static void ConfigureClient(HttpClient client) => client.Timeout = Timeout;

    public async Task<bool> VerifyAsync(
        string token, string remoteIp, string expectedHostname, CancellationToken cancellationToken)
    {
        var secret = options.Turnstile.SecretKey;
        if (string.IsNullOrWhiteSpace(secret))
        {
            LogNoKey(logger);
            throw Unavailable("Turnstile is not configured.");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("secret", secret.Trim()),
            new("response", token),
        };
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            form.Add(new("remoteip", remoteIp));
        }

        string body;
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var content = new FormUrlEncodedContent(form);
            using var response = await client.PostAsync(options.Turnstile.VerifyUrl, content, cancellationToken);
            if ((int)response.StatusCode >= 500)
            {
                LogUnreachable(logger, $"HTTP {(int)response.StatusCode}");
                throw Unavailable("Turnstile answered with a server error.");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                // Un 4xx es que NUESTRO pedido está mal (llave rota): no es un "no" sobre el visitante.
                LogUnreachable(logger, $"HTTP {(int)response.StatusCode}");
                throw Unavailable("Turnstile rejected the verification request.");
            }

            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            LogUnreachable(logger, "network error");
            throw Unavailable("Turnstile could not be reached.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout se reporta como cancelación; la del llamador sí se propaga.
            LogUnreachable(logger, "timeout");
            throw Unavailable("Turnstile timed out.");
        }

        return Evaluate(body, expectedHostname);
    }

    // Con HTTP 200, Cloudflare también entrega «la llave está mal» y «yo fallé»: eso no dice nada del visitante.
    private static readonly string[] ProviderFailureCodes = ["invalid-input-secret", "missing-input-secret", "internal-error"];

    private bool Evaluate(string body, string expectedHostname)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                LogUnreachable(logger, "unreadable answer");
                throw Unavailable("Turnstile answered something unreadable.");
            }

            var success = root.TryGetProperty("success", out var flag) && flag.ValueKind == JsonValueKind.True;
            var hostname = root.TryGetProperty("hostname", out var host) && host.ValueKind == JsonValueKind.String ? host.GetString() : null;
            var hostnameMatch = !enforceHostname
                || string.Equals(hostname?.Trim(), expectedHostname.Trim(), StringComparison.OrdinalIgnoreCase);
            if (success && hostnameMatch)
            {
                return true;
            }

            var codes = root.TryGetProperty("error-codes", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(code => code.ToString()).Take(5).ToArray()
                : [];
            if (!success && codes.Any(code => ProviderFailureCodes.Contains(code, StringComparer.OrdinalIgnoreCase)))
            {
                // Sólo el código, nunca la llave ni el token.
                var provider = string.Join(',', codes);
                LogUnreachable(logger, $"provider error {provider}");
                throw Unavailable("Turnstile reported a configuration or provider error.");
            }

            var joined = string.Join(',', codes);
            LogRefused(logger, success, hostnameMatch, joined);
            return false;
        }
        catch (JsonException)
        {
            LogUnreachable(logger, "unreadable answer");
            throw Unavailable("Turnstile answered something unreadable.");
        }
    }

    private static ServiceUnavailableException Unavailable(string message) =>
        new(StoreErrorCodes.CaptchaUnavailable, message);
}

/// <summary>Sólo con <c>Store:Turnstile:Enabled=false</c>, que el arranque prohíbe fuera de Development y Testing.</summary>
internal sealed class PassThroughCaptchaVerifier : ICaptchaVerifier
{
    public Task<bool> VerifyAsync(string token, string remoteIp, string expectedHostname, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
