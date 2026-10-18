using System.Security.Cryptography;
using System.Text;

namespace Modules.Integrations.Application.Shopify;

/// <summary>
/// Spec 2026-10-15 §4.3 y §12: las dos firmas de Shopify con el client secret de la app. La de la query (instalación y
/// callback): todos los parámetros menos <c>hmac</c>, ordenados ordinal, <c>k=v</c> unidos con <c>&amp;</c>,
/// HMAC-SHA256 en hex. La del cuerpo (webhook, <c>X-Shopify-Hmac-Sha256</c>): HMAC-SHA256 de los bytes crudos en base64.
/// Las dos comparan en tiempo constante y nunca lanzan ante una firma mal formada: devuelven <c>false</c>.
/// </summary>
public static class ShopifyHmac
{
    public const string QueryHmacKey = "hmac";

    private const int Sha256Bytes = 32;

    public static bool VerifyQuery(IReadOnlyDictionary<string, string> query, string secret)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrEmpty(secret)
            || !query.TryGetValue(QueryHmacKey, out var hmac)
            || !TryFromHex(hmac, out var expected))
        {
            return false;
        }

        var message = string.Join(
            '&',
            query
                .Where(pair => !string.Equals(pair.Key, QueryHmacKey, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
        var actual = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool VerifyBody(ReadOnlySpan<byte> body, string base64Signature, string secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(base64Signature))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[Sha256Bytes];
        if (!Convert.TryFromBase64String(base64Signature, expected, out var written) || written != Sha256Bytes)
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[Sha256Bytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body, actual);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static bool TryFromHex(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is not { Length: Sha256Bytes * 2 })
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
