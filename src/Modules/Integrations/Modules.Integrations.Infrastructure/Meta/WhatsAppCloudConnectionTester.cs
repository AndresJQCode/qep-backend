using System.Text.Json;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.Verification;

namespace Modules.Integrations.Infrastructure.Meta;

/// <summary>Spec 2026-10-09 §6.1, «Probador whatsapp-cloud»: <c>GET /{v}/{phoneNumberId}?fields=…</c> con
/// <c>Authorization: Bearer</c>. Un 200 vale con cualquier <c>status</c> (D-M14): el <c>status</c> no
/// decide. <c>190</c> → <c>token_expired</c>; <c>133010</c> → <c>number_unregistered</c>; otro
/// 400/401/403 → <c>credentials_rejected</c>; timeout, red, 5xx y cualquier otro status → «no pude
/// verificar».</summary>
internal sealed class WhatsAppCloudConnectionTester(MetaGraphClient graph) : IProviderConnectionTester
{
    public const string Fields = "display_phone_number,verified_name,quality_rating,status";

    private const int TokenExpiredCode = 190;
    private const int NumberUnregisteredCode = 133010;

    public string ProviderKey => IntegrationProviders.WhatsAppCloud.Key;

    public async Task<ConnectionTestResult> TestAsync(
        IReadOnlyDictionary<string, string> fields,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken)
    {
        // Sin token no hay a quién preguntarle: es una credencial rechazada, no un 500.
        if (!secrets.TryGetValue(WhatsAppCloudFieldKeys.AccessToken, out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
        {
            return ConnectionTestResult.RejectedWith(ConnectionFailureCodes.CredentialsRejected);
        }

        var phoneNumberId = fields.GetValueOrDefault(WhatsAppCloudFieldKeys.PhoneNumberId) ?? string.Empty;
        using var request = new HttpRequestMessage(
            HttpMethod.Get, graph.Path($"{Uri.EscapeDataString(phoneNumberId)}?fields={Fields}"));
        var response = await graph.SendAsync("phone-number", request, accessToken, cancellationToken);
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
            return response.Error?.Code switch
            {
                TokenExpiredCode => ConnectionTestResult.RejectedWith(ConnectionFailureCodes.TokenExpired),
                NumberUnregisteredCode => ConnectionTestResult.RejectedWith(ConnectionFailureCodes.NumberUnregistered),
                _ => ConnectionTestResult.RejectedWith(ConnectionFailureCodes.CredentialsRejected),
            };
        }

        return ConnectionTestResult.Unreachable($"http_{status}");
    }

    /// <summary>Lo que Meta devuelve sobre el número, con las claves del catálogo. Lo que falte se omite.</summary>
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

            Copy(root, "display_phone_number", WhatsAppCloudFieldKeys.DisplayPhoneNumber, refreshed);
            Copy(root, "verified_name", WhatsAppCloudFieldKeys.VerifiedName, refreshed);
            Copy(root, "quality_rating", WhatsAppCloudFieldKeys.QualityRating, refreshed);
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
