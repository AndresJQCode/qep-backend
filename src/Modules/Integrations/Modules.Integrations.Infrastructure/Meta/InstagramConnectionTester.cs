using System.Text.Json;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Spec 2026-10-14 §6.1, probador de <c>instagram</c>: <c>GET /{v}/me?fields=id,username,name</c> con el token.
/// Un 200 refresca <c>username</c> y <c>accountName</c>; <c>190</c> → <c>token_expired</c>; otro 400/401/403 →
/// <c>credentials_rejected</c>; timeout, red, 5xx y cualquier otro status → «no pude verificar».</summary>
internal sealed class InstagramConnectionTester(InstagramGraphClient graph) : IProviderConnectionTester
{
    public const string Fields = "id,username,name";

    private const int TokenExpiredCode = 190;

    public string ProviderKey => IntegrationProviders.Instagram.Key;

    public async Task<ConnectionTestResult> TestAsync(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        if (!secrets.TryGetValue(InstagramFieldKeys.AccessToken, out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
        {
            return ConnectionTestResult.RejectedWith(ConnectionFailureCodes.CredentialsRejected);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, graph.Path($"me?fields={Fields}"));
        var response = await graph.SendAsync("me", request, accessToken, cancellationToken);
        return Classify(response);
    }

    internal static ConnectionTestResult Classify(MetaGraphResponse response)
    {
        if (response.UnreachableReason is { } reason)
        {
            return ConnectionTestResult.Unreachable(reason);
        }

        if (response.IsSuccess)
        {
            return ConnectionTestResult.OkWith(ReadFields(response.Body));
        }

        var status = (int)response.Status;
        if (status is 400 or 401 or 403)
        {
            return response.Error?.Code == TokenExpiredCode
                ? ConnectionTestResult.RejectedWith(ConnectionFailureCodes.TokenExpired)
                : ConnectionTestResult.RejectedWith(ConnectionFailureCodes.CredentialsRejected);
        }

        return ConnectionTestResult.Unreachable($"http_{status}");
    }

    internal static Dictionary<string, string> ReadFields(string body)
    {
        var refreshed = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return refreshed;
            }

            Copy(root, "username", InstagramFieldKeys.Username, refreshed);
            Copy(root, "name", InstagramFieldKeys.AccountName, refreshed);
        }
        catch (JsonException)
        {
            // Un 200 con un cuerpo que no es JSON sigue siendo Ok: no hay nada que refrescar.
        }

        return refreshed;
    }

    private static void Copy(JsonElement root, string from, string to, Dictionary<string, string> into)
    {
        if (root.TryGetProperty(from, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
        {
            into[to] = text;
        }
    }
}
