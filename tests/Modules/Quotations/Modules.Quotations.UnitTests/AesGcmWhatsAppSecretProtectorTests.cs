using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.SecretProtection;
using Modules.Quotations.Infrastructure.Whatsapp;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Spec 2026-10-07, «Decisión: AES-256-GCM»: nonce aleatorio por cifrado, AAD atado al tenant,
/// llave elegida por el <c>KeyId</c> de la fila, y errores que nombran la clave de configuración
/// y nunca un valor.
/// </summary>
public sealed class AesGcmWhatsAppSecretProtectorTests
{
    private static readonly byte[] K1 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] K2 = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private const string Token = "zenvia-token-SENTINEL-123";

    private static AesGcmWhatsAppSecretProtector Protector(string? active, params (string Id, byte[]? Key)[] keys)
    {
        var options = new SecretProtectionOptions { ActiveKeyId = active };
        foreach (var (id, key) in keys)
        {
            options.Keys[id] = key is null ? "" : Convert.ToBase64String(key);
        }

        return new AesGcmWhatsAppSecretProtector(Options.Create(options));
    }

    [Fact]
    public void ProtectThenUnprotectRoundTrips()
    {
        var protector = Protector("k1", ("k1", K1));

        var secret = protector.Protect(TenantId, Token);

        Assert.Equal("k1", secret.KeyId);
        Assert.Equal(Token, protector.Unprotect(TenantId, secret));
    }

    [Fact]
    public void TwoProtectionsOfTheSameTextDiffer()
    {
        var protector = Protector("k1", ("k1", K1));

        var first = protector.Protect(TenantId, Token);
        var second = protector.Protect(TenantId, Token);

        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.Equal(12 + Encoding.UTF8.GetByteCount(Token) + 16, first.Ciphertext.Length);
    }

    [Fact]
    public void AnotherTenantCannotUnprotect()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(TenantId, Token);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(Guid.CreateVersion7(), secret));
    }

    // El AAD es exactamente el UTF-8 de "quotations.whatsapp.api_token:" + tenantId en formato D:
    // un texto cifrado a mano con ese AAD descifra.
    [Fact]
    public void TheAssociatedDataIsThePrefixAndTheTenantInFormatD()
    {
        var protector = Protector("k1", ("k1", K1));
        var plaintext = Encoding.UTF8.GetBytes(Token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(K1, 16))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag,
                Encoding.UTF8.GetBytes("quotations.whatsapp.api_token:" + TenantId.ToString("D")));
        }

        var manual = new ProtectedSecret("k1", [.. nonce, .. ciphertext, .. tag]);

        Assert.Equal(Token, protector.Unprotect(TenantId, manual));
    }

    [Fact]
    public void AnUnknownKeyIdThrowsNamingTheConfigurationKeyAndNoKey()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = new ProtectedSecret("k9", new byte[40]);

        var error = Assert.Throws<InvalidOperationException>(() => protector.Unprotect(TenantId, secret));

        Assert.Contains("Quotations:SecretProtection:Keys:k9", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(K1), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterChangingTheActiveKeyOldSecretsReadWithTheirKeyAndNewOnesUseTheNewKey()
    {
        var old = Protector("k1", ("k1", K1)).Protect(TenantId, Token);
        var rotated = Protector("k2", ("k1", K1), ("k2", K2));

        var fresh = rotated.Protect(TenantId, Token);

        Assert.Equal(Token, rotated.Unprotect(TenantId, old));
        Assert.Equal("k2", fresh.KeyId);
        Assert.Equal(Token, rotated.Unprotect(TenantId, fresh));
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
        var error = Assert.Throws<InvalidOperationException>(() => protector.Protect(TenantId, Token));
        Assert.Contains("Quotations:SecretProtection:ActiveKeyId", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryUnprotectIsFalseWithoutThrowingWhenTheKeyIsMissing()
    {
        var secret = Protector("k1", ("k1", K1)).Protect(TenantId, Token);

        Assert.False(Protector("k2", ("k2", K2)).TryUnprotect(TenantId, secret, out var plaintext));
        Assert.Null(plaintext);
    }

    [Fact]
    public void TryUnprotectIsFalseWhenAByteWasAltered()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(TenantId, Token);
        var damaged = secret.Ciphertext.ToArray();
        damaged[20] ^= 0xFF;

        Assert.False(protector.TryUnprotect(TenantId, secret with { Ciphertext = damaged }, out _));
    }

    [Fact]
    public void TryUnprotectIsFalseForAnotherTenant()
    {
        var protector = Protector("k1", ("k1", K1));
        var secret = protector.Protect(TenantId, Token);

        Assert.False(protector.TryUnprotect(Guid.CreateVersion7(), secret, out _));
    }

    [Fact]
    public void TheCiphertextDoesNotContainTheTokenBytes()
    {
        var secret = Protector("k1", ("k1", K1)).Protect(TenantId, Token);

        Assert.True(secret.Ciphertext.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Token)) < 0);
    }
}
