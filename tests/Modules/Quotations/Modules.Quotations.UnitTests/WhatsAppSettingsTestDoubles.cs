using System.Security.Cryptography;
using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>La configuración de WhatsApp en memoria (spec 2026-10-07). Las dos lecturas devuelven
/// la misma instancia: lo que la prueba mira es qué hizo el handler con ella.</summary>
internal sealed class InMemoryTenantWhatsAppSettingsRepository : ITenantWhatsAppSettingsRepository
{
    public List<TenantWhatsAppSettings> Rows { get; } = [];

    public int FindCalls { get; private set; }

    public Task<TenantWhatsAppSettings?> FindAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        FindCalls++;
        return Task.FromResult(Rows.FirstOrDefault(row => row.TenantId == tenantId));
    }

    public Task<TenantWhatsAppSettings?> FindReadOnlyAsync(Guid tenantId, CancellationToken cancellationToken) =>
        FindAsync(tenantId, cancellationToken);

    public void Add(TenantWhatsAppSettings settings) => Rows.Add(settings);
}

/// <summary>
/// Un protector sin criptografía: recuerda qué texto corresponde a cada arreglo de bytes (por
/// referencia) y "descifra" sólo si conoce la llave del <c>KeyId</c> y los bytes. Con eso se
/// simulan los tres casos que importan —legible, llave retirada, bytes dañados— sin depender de
/// AES, que tiene sus propias pruebas.
/// </summary>
internal sealed class FakeWhatsAppSecretProtector : IWhatsAppSecretProtector
{
    private readonly Dictionary<byte[], string> _plaintexts = new(ReferenceEqualityComparer.Instance);

    public string? ActiveKeyId { get; set; } = "k2";

    public HashSet<string> KnownKeys { get; } = ["k1", "k2"];

    /// <summary>Lo que se mandó a cifrar, en orden.</summary>
    public List<string> ProtectedPlaintexts { get; } = [];

    public bool HasKey(string keyId) => KnownKeys.Contains(keyId);

    public ProtectedSecret Protect(Guid tenantId, string plaintext)
    {
        var keyId = ActiveKeyId ?? throw new InvalidOperationException(
            "Quotations:SecretProtection:ActiveKeyId is not configured.");
        ProtectedPlaintexts.Add(plaintext);
        return Seed(keyId, plaintext);
    }

    /// <summary>Un secreto legible, cifrado "con" <paramref name="keyId"/>, sin pasar por
    /// <see cref="ProtectedPlaintexts"/>: lo que ya estaba guardado antes de la prueba.</summary>
    public ProtectedSecret Seed(string keyId, string plaintext)
    {
        var bytes = RandomNumberGenerator.GetBytes(16);
        _plaintexts[bytes] = plaintext;
        return new ProtectedSecret(keyId, bytes);
    }

    /// <summary>Bytes que no descifran con ninguna llave: un tag inválido.</summary>
    public static ProtectedSecret Garbage(string keyId) => new(keyId, RandomNumberGenerator.GetBytes(16));

    public string Unprotect(Guid tenantId, ProtectedSecret secret) =>
        TryUnprotect(tenantId, secret, out var plaintext)
            ? plaintext!
            : throw new CryptographicException("The protected secret cannot be decrypted.");

    public bool TryUnprotect(Guid tenantId, ProtectedSecret secret, out string? plaintext)
    {
        plaintext = null;
        return KnownKeys.Contains(secret.KeyId) && _plaintexts.TryGetValue(secret.Ciphertext, out plaintext);
    }
}
