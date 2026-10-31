using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Modules.ApiAccess.Domain;

/// <summary>
/// El material de una clave recién generada: <c>qep_{prefijo}_{secreto}</c>. Solo existe en memoria
/// mientras se crea la clave; a la base van el prefijo y el hash del secreto, nunca el texto completo.
/// </summary>
public sealed partial class ApiKeyMaterial
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // 43 caracteres base62 son más de 256 bits y, a diferencia de convertir bytes, no introducen sesgo.
    private const int SecretLength = 43;

    // "qep_" + 8 + "_" + 43: corte previo por longitud para no pasar texto enorme por la expresión.
    private const int PlaintextLength = 4 + ApiKey.PrefixLength + 1 + SecretLength;

    private ApiKeyMaterial(string prefix, string secret)
    {
        Prefix = prefix;
        Secret = secret;
        SecretHash = HashOf(secret);
    }

    public string Prefix { get; }

    public string Secret { get; }

    public byte[] SecretHash { get; }

    public string Plaintext => $"qep_{Prefix}_{Secret}";

    // Un log, una excepción o el depurador llaman a ToString: nunca puede llevar el secreto.
    public override string ToString() => $"ApiKeyMaterial {{ Prefix = {Prefix} }}";

    public static ApiKeyMaterial Generate() => new(
        RandomNumberGenerator.GetString(Alphabet, ApiKey.PrefixLength),
        RandomNumberGenerator.GetString(Alphabet, SecretLength));

    public static bool TryParse(string? presented, out string prefix, out string secret)
    {
        prefix = string.Empty;
        secret = string.Empty;

        if (presented is null || presented.Length != PlaintextLength)
        {
            return false;
        }

        var match = KeyShape().Match(presented);
        if (!match.Success)
        {
            return false;
        }

        prefix = match.Groups["prefix"].Value;
        secret = match.Groups["secret"].Value;
        return true;
    }

    public static bool Matches(string secret, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(HashOf(secret), expectedHash);

    private static byte[] HashOf(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    [GeneratedRegex("^qep_(?<prefix>[0-9A-Za-z]{8})_(?<secret>[0-9A-Za-z]{43})$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyShape();
}
