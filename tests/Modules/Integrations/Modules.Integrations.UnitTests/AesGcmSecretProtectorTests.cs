using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Integrations.Domain;
using Modules.Integrations.Infrastructure.SecretProtection;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Secreto en reposo» (viene de 6612298): nonce aleatorio por cifrado, AAD atado a la
/// conexión y al campo (D7), llave elegida por el <c>KeyId</c> de la fila, y errores que nombran la
/// clave de configuración y nunca un valor.
/// </summary>
public sealed class AesGcmSecretProtectorTests
{
    private static readonly byte[] K1 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] K2 = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    private static readonly Guid ConnectionId = Guid.CreateVersion7();
    private const string FieldKey = "apiToken";
    private const string Token = "zenvia-token-SENTINEL-123";

    private static AesGcmSecretProtector Protector(string? active, params (string Id, byte[]? Key)[] keys)
    {
        var options = new SecretProtectionOptions { ActiveKeyId = active };
        foreach (var (id, key) in keys)
        {
            options.Keys[id] = key is null ? "" : Convert.ToBase64String(key);
        }

        return new AesGcmSecretProtector(Options.Create(options));
    }

    [Fact]
    public void ProtectThenUnprotectRoundTrips()
    {
        var protector = Protector("k1", ("k1", K1));

        var secret = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.Equal("k1", secret.KeyId);
        Assert.Equal(Token, protector.Unprotect(ConnectionId, FieldKey, secret));
    }

    [Fact]
    public void TwoProtectionsOfTheSameTextDiffer()
    {
        var protector = Protector("k1", ("k1", K1));

        var first = protector.Protect(ConnectionId, FieldKey, Token);
        var second = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.Equal(12 + Encoding.UTF8.GetByteCount(Token) + 16, first.Ciphertext.Length);
    }

    [Fact]
    public void AnotherConnectionCannotUnprotect()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.CreateVersion7(), FieldKey, secret));
    }

    // D7: un texto cifrado copiado a otro campo de la misma conexión tampoco descifra.
    [Fact]
    public void AnotherFieldCannotUnprotect()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(ConnectionId, "otherSecret", secret));
    }

    // El AAD es exactamente "integrations.connection:" + connectionId en formato D + ":" + fieldKey: un
    // texto cifrado a mano con ese AAD descifra.
    [Fact]
    public void TheAssociatedDataIsThePrefixTheConnectionInFormatDAndTheField()
    {
        var protector = Protector("k1", ("k1", K1));
        var plaintext = Encoding.UTF8.GetBytes(Token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(K1, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag,
                Encoding.UTF8.GetBytes("integrations.connection:" + ConnectionId.ToString("D") + ":" + FieldKey));
        }

        var manual = new ProtectedSecret("k1", [.. nonce, .. ciphertext, .. tag]);

        Assert.Equal(Token, protector.Unprotect(ConnectionId, FieldKey, manual));
    }

    [Fact]
    public void AnUnknownKeyIdThrowsNamingTheConfigurationKeyAndNoKey()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = new ProtectedSecret("k9", new byte[40]);

        var error = Assert.Throws<InvalidOperationException>(() => protector.Unprotect(ConnectionId, FieldKey, secret));

        Assert.Contains("Integrations:SecretProtection:Keys:k9", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(K1), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterChangingTheActiveKeyOldSecretsReadWithTheirKeyAndNewOnesUseTheNewKey()
    {
        var old = Protector("k1", ("k1", K1)).Protect(ConnectionId, FieldKey, Token);
        var rotated = Protector("k2", ("k1", K1), ("k2", K2));

        var fresh = rotated.Protect(ConnectionId, FieldKey, Token);

        Assert.Equal(Token, rotated.Unprotect(ConnectionId, FieldKey, old));
        Assert.Equal("k2", fresh.KeyId);
        Assert.Equal(Token, rotated.Unprotect(ConnectionId, FieldKey, fresh));
    }

    [Fact]
    public void HasKeyTreatsAnEmptyValueAsAbsent()
    {
        var protector = Protector("k1", ("k1", K1), ("k2", null));

        Assert.True(protector.HasKey("k1"));
        Assert.False(protector.HasKey("k2"));
        Assert.False(protector.HasKey("k3"));
    }

    [Fact]
    public void AnEmptyActiveKeyIdIsNullAndProtectThrows()
    {
        var protector = Protector("", ("k1", K1));

        Assert.Null(protector.ActiveKeyId);
        var error = Assert.Throws<InvalidOperationException>(() => protector.Protect(ConnectionId, FieldKey, Token));
        Assert.Contains("Integrations:SecretProtection:ActiveKeyId", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryUnprotectIsFalseWithoutThrowingWhenTheKeyIsMissing()
    {
        var secret = Protector("k1", ("k1", K1)).Protect(ConnectionId, FieldKey, Token);

        Assert.False(Protector("k2", ("k2", K2)).TryUnprotect(ConnectionId, FieldKey, secret, out var plaintext));
        Assert.Null(plaintext);
    }

    [Fact]
    public void TryUnprotectIsFalseWhenAByteWasAltered()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(ConnectionId, FieldKey, Token);
        var damaged = secret.Ciphertext.ToArray();
        damaged[20] ^= 0xFF;

        Assert.False(protector.TryUnprotect(ConnectionId, FieldKey, secret with { Ciphertext = damaged }, out _));
    }

    [Fact]
    public void TryUnprotectIsFalseForAnotherConnectionOrField()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(ConnectionId, FieldKey, Token);

        Assert.False(protector.TryUnprotect(Guid.CreateVersion7(), FieldKey, secret, out _));
        Assert.False(protector.TryUnprotect(ConnectionId, "otherSecret", secret, out _));
    }

    [Fact]
    public void TheCiphertextDoesNotContainTheTokenBytes()
    {
        var secret = Protector("k1", ("k1", K1)).Protect(ConnectionId, FieldKey, Token);

        Assert.True(secret.Ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Token)) < 0);
    }
}
