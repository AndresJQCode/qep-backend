using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.SecretProtection;

namespace Modules.Quotations.Infrastructure.Whatsapp;

/// <summary>
/// AES-256-GCM en la caja de .NET, sin Data Protection (spec 2026-10-07, decisión 1): la llave
/// vive fuera de la base. Formato: <c>nonce(12) || ciphertext || tag(16)</c>, nonce aleatorio por
/// cifrado. AAD = UTF-8 de <c>"quotations.whatsapp.api_token:" + tenantId.ToString("D")</c>:
/// una fila copiada a otro tenant por SQL no descifra. El formato <c>D</c> es explícito porque
/// cualquier otro no descifraría lo ya guardado.
///
/// Singleton: lee las opciones una vez, al arrancar, que es cuando se validaron.
/// </summary>
internal sealed class AesGcmWhatsAppSecretProtector(IOptions<SecretProtectionOptions> options)
    : IWhatsAppSecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string AssociatedDataPrefix = "quotations.whatsapp.api_token:";

    private readonly SecretProtectionOptions settings = options.Value;

    public string? ActiveKeyId => settings.EffectiveActiveKeyId;

    public bool HasKey(string keyId) => settings.KeyValue(keyId) is not null;

    public ProtectedSecret Protect(Guid tenantId, string plaintext)
    {
        var keyId = ActiveKeyId ?? throw new InvalidOperationException(
            $"{SecretProtectionOptions.SectionName}:ActiveKeyId is not configured: "
            + "the WhatsApp API key cannot be encrypted.");
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
            AssociatedData(tenantId));

        return new ProtectedSecret(keyId, output);
    }

    public string Unprotect(Guid tenantId, ProtectedSecret secret)
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
            AssociatedData(tenantId));

        return Encoding.UTF8.GetString(plain);
    }

    public bool TryUnprotect(Guid tenantId, ProtectedSecret secret, out string? plaintext)
    {
        try
        {
            plaintext = Unprotect(tenantId, secret);
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
            + "a WhatsApp API key stored with that key cannot be used.");
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

    private static byte[] AssociatedData(Guid tenantId) =>
        Encoding.UTF8.GetBytes(AssociatedDataPrefix + tenantId.ToString("D"));
}
