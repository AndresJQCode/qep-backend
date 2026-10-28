using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Spec 2026-10-14 §8.1 contra Meta. El <c>client_secret</c> va en el <b>cuerpo</b> del canje (form) y en la
/// query del token largo, nunca en un log: <see cref="MetaGraphClient.SendWithAsync"/> registra sólo status y
/// códigos, y OpenTelemetry redacta los valores del query.</summary>
internal sealed class InstagramLoginGateway(InstagramGraphClient graph, IOptions<MetaInstagramOptions> options) : IInstagramLoginGateway
{
    public const string MeFields = "id,username,name,account_type";

    public async Task<GraphResult<InstagramShortLivedToken>> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken)
    {
        var app = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(InstagramGraphClient.CodeExchangeUrl, UriKind.Absolute))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["client_id"] = app.AppId?.Trim() ?? string.Empty,
                ["client_secret"] = app.AppSecret ?? string.Empty,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri,
                ["code"] = code,
            }),
        };
        var response = await graph.SendAsync("oauth", request, accessToken: null, cancellationToken);
        if (!response.IsSuccess)
        {
            return new GraphResult<InstagramShortLivedToken>(null, Failure(response));
        }

        return ReadShortLived(response);
    }

    public Task<GraphResult<InstagramLongLivedToken>> ExchangeLongLivedAsync(string shortLivedToken, CancellationToken cancellationToken) =>
        LongLivedAsync(
            "long-lived",
            $"access_token?grant_type=ig_exchange_token&client_secret={Uri.EscapeDataString(options.Value.AppSecret ?? string.Empty)}&access_token={Uri.EscapeDataString(shortLivedToken)}",
            cancellationToken);

    public Task<GraphResult<InstagramLongLivedToken>> RefreshAsync(string longLivedToken, CancellationToken cancellationToken) =>
        LongLivedAsync(
            "refresh",
            $"refresh_access_token?grant_type=ig_refresh_token&access_token={Uri.EscapeDataString(longLivedToken)}",
            cancellationToken);

    public async Task<GraphResult<InstagramAccount>> GetMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, graph.Path($"me?fields={MeFields}"));
        var response = await graph.SendAsync("me", request, accessToken, cancellationToken);
        if (!response.IsSuccess)
        {
            return new GraphResult<InstagramAccount>(null, Failure(response));
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadString(root, "id") is not { Length: > 0 } id)
            {
                return new GraphResult<InstagramAccount>(null, new GraphFailure((int)response.Status, null, null, "unreadable"));
            }

            return new GraphResult<InstagramAccount>(
                new InstagramAccount(id, ReadString(root, "username"), ReadString(root, "name"), ReadString(root, "account_type")), null);
        }
        catch (JsonException)
        {
            return new GraphResult<InstagramAccount>(null, new GraphFailure((int)response.Status, null, null, "unreadable"));
        }
    }

    public async Task<GraphResult<bool>> SubscribeAsync(string igUserId, string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, graph.Path($"{Uri.EscapeDataString(igUserId)}/subscribed_apps?subscribed_fields={InstagramLogin.WebhookFields}"));
        var response = await graph.SendAsync("subscribe", request, accessToken, cancellationToken);
        return response.IsSuccess ? new GraphResult<bool>(true, null) : new GraphResult<bool>(false, Failure(response));
    }

    // Los dos endpoints de token largo (sin versión en la ruta, como los documenta Meta) devuelven la misma forma.
    private async Task<GraphResult<InstagramLongLivedToken>> LongLivedAsync(string operation, string relative, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relative);
        var response = await graph.SendAsync(operation, request, accessToken: null, cancellationToken);
        if (!response.IsSuccess)
        {
            return new GraphResult<InstagramLongLivedToken>(null, Failure(response));
        }

        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadString(root, "access_token") is not { Length: > 0 } token)
            {
                return new GraphResult<InstagramLongLivedToken>(null, new GraphFailure((int)response.Status, null, null, "no_token"));
            }

            var expiresIn = root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number && expires.TryGetInt64(out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;
            return new GraphResult<InstagramLongLivedToken>(new InstagramLongLivedToken(token, expiresIn), null);
        }
        catch (JsonException)
        {
            return new GraphResult<InstagramLongLivedToken>(null, new GraphFailure((int)response.Status, null, null, "unreadable"));
        }
    }

    /// <summary>Business Login devuelve <c>{ "data": [ { access_token, user_id, permissions } ] }</c>; la forma vieja (Basic
    /// Display) es el mismo objeto en la raíz. <c>permissions</c> viene como lista o como texto separado por comas, y
    /// <c>user_id</c> como número o texto. Se aceptan todas.</summary>
    internal static GraphResult<InstagramShortLivedToken> ReadShortLived(MetaGraphResponse response)
    {
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            var item = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
                ? data[0]
                : root;
            if (item.ValueKind != JsonValueKind.Object || ReadString(item, "access_token") is not { Length: > 0 } token)
            {
                return new GraphResult<InstagramShortLivedToken>(null, new GraphFailure((int)response.Status, null, null, "no_token"));
            }

            var userId = ReadString(item, "user_id") ?? string.Empty;
            var permissions = new List<string>();
            if (item.TryGetProperty("permissions", out var granted))
            {
                if (granted.ValueKind == JsonValueKind.Array)
                {
                    permissions.AddRange(granted.EnumerateArray().Where(scope => scope.ValueKind == JsonValueKind.String).Select(scope => scope.GetString()!));
                }
                else if (granted.ValueKind == JsonValueKind.String)
                {
                    permissions.AddRange((granted.GetString() ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }

            return new GraphResult<InstagramShortLivedToken>(new InstagramShortLivedToken(token, userId, permissions), null);
        }
        catch (JsonException)
        {
            return new GraphResult<InstagramShortLivedToken>(null, new GraphFailure((int)response.Status, null, null, "unreadable"));
        }
    }

    private static GraphFailure Failure(MetaGraphResponse response) =>
        new((int)response.Status, response.Error?.Code, response.Error?.Subcode, response.UnreachableReason ?? $"http_{(int)response.Status}");

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}
