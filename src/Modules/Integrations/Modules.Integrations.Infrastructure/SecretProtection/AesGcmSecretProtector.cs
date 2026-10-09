using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.Infrastructure.SecretProtection;

/// <summary>
/// AES-256-GCM en la caja de .NET, sin Data Protection (viene de 6612298): la llave vive fuera de la
/// base. Formato: <c>nonce(12) || ciphertext || tag(16)</c>, nonce aleatorio por cifrado. AAD = UTF-8
/// de <c>"integrations.connection:" + connectionId.ToString("D") + ":" + fieldKey</c> (spec
/// 2026-10-08, D7): un texto cifrado copiado a otra conexión o a otro campo no descifra. El prefijo
/// viejo (<c>quotations.whatsapp.api_token:</c>) no se conserva: nada cifrado con él llegó a producción.
///
/// Singleton: lee las opciones una vez, al arrancar, que es cuando se validaron.
/// </summary>
internal sealed class AesGcmSecretProtector(IOptions<SecretProtectionOptions> options) : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string AssociatedDataPrefix = "integrations.connection:";

    private readonly SecretProtectionOptions settings = options.Value;

    public string? ActiveKeyId => settings.EffectiveActiveKeyId;

    public bool HasKey(string keyId) => settings.KeyValue(keyId) is not null;

    public ProtectedSecret Protect(Guid connectionId, string fieldKey, string plaintext)
    {
        var keyId = ActiveKeyId ?? throw new InvalidOperationException(
            $"{SecretProtectionOptions.SectionName}:ActiveKeyId is not configured: "
            + "the connection secret cannot be encrypted.");
        var key = KeyBytes(keyId);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(
            nonce,
            plain,
            output.AsSpan(NonceSize, plain.Length),
            output.AsSpan(NonceSize + plain.Length, TagSize),
            AssociatedData(connectionId, fieldKey));

        return new ProtectedSecret(keyId, output);
    }

    public string Unprotect(Guid connectionId, string fieldKey, ProtectedSecret secret)
    {
        var key = KeyBytes(secret.KeyId);
        var data = secret.Ciphertext;
        if (data.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("The protected secret is malformed.");
        }

        var length = data.Length - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(
            data.AsSpan(0, NonceSize),
            data.AsSpan(NonceSize, length),
            data.AsSpan(NonceSize + length, TagSize),
            plain,
            AssociatedData(connectionId, fieldKey));

        return Encoding.UTF8.GetString(plain);
    }

    public bool TryUnprotect(
        Guid connectionId, string fieldKey, ProtectedSecret secret, [NotNullWhen(true)] out string? plaintext)
    {
        try
        {
            plaintext = Unprotect(connectionId, fieldKey, secret);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or CryptographicException)
        {
            plaintext = null;
            return false;
        }
    }

    private byte[] KeyBytes(string keyId)
    {
        var value = settings.KeyValue(keyId) ?? throw new InvalidOperationException(
            $"{SecretProtectionOptions.KeyPath(keyId)} is not configured: "
            + "a connection secret stored with that key cannot be used.");
        try
        {
            var key = Convert.FromBase64String(value);
            if (key.Length == 32)
            {
                return key;
            }
        }
        catch (FormatException)
        {
            // Cae al mensaje de abajo: el de FormatException no nombra la clave.
        }

        throw new InvalidOperationException(
            $"{SecretProtectionOptions.KeyPath(keyId)} must be 32 bytes encoded in base64.");
    }

    private static byte[] AssociatedData(Guid connectionId, string fieldKey) =>
        Encoding.UTF8.GetBytes(AssociatedDataPrefix + connectionId.ToString("D") + ":" + fieldKey);
}
