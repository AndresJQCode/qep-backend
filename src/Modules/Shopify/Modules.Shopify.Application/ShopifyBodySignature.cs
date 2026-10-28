using System.Security.Cryptography;
using System.Text;

namespace Modules.Shopify.Application;

/// <summary>
/// Spec 2026-10-15 §5.1 y §12: <c>X-Shopify-Hmac-Sha256</c> es el HMAC-SHA256 de los bytes crudos del cuerpo con el client secret,
/// en base64. Copia de <c>ShopifyHmac.VerifyBody</c> de Integrations, que este módulo no puede referenciar: dos copias de diez
/// líneas antes que un acoplamiento prohibido, amarradas por los mismos vectores en las dos pruebas. Compara en tiempo constante y
/// nunca lanza ante una firma mal formada.
/// </summary>
public static class ShopifyBodySignature
{
    private const int Sha256Bytes = 32;

    public static bool Verify(ReadOnlySpan<byte> body, string? base64Signature, string? secret)
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
}
