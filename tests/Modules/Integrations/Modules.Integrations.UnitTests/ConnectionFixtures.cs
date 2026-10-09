using System.Text;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>Valores fijos de las pruebas del agregado. Ninguno es un secreto real.</summary>
internal static class ConnectionFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Later = Now.AddHours(1);
    public static readonly Guid TenantId = Guid.CreateVersion7();
    public static readonly Guid MemberId = Guid.CreateVersion7();
    public const string Token = "zenvia-token-TEST-1";
    public const string FromNumber = "573001234567";

    public static Dictionary<string, string> Fields(string fromNumber = FromNumber) =>
        new(StringComparer.Ordinal) { [ZenviaFieldKeys.FromNumber] = fromNumber };

    public static Dictionary<string, string> Secrets(string token = Token) =>
        new(StringComparer.Ordinal) { [ZenviaFieldKeys.ApiToken] = token };

    public static Dictionary<string, string> None() => new(StringComparer.Ordinal);

    public static IntegrationConnection Create(RecordingSealer sealer, string name = "WhatsApp sede norte") =>
        IntegrationConnection.Create(
            IntegrationProviders.Zenvia, TenantId, name, Fields(), Secrets(), sealer.Seal, MemberId, Now);
}

/// <summary>
/// Un "cifrado" legible: deja ver con qué conexión, campo y texto se selló. El dominio nunca descifra,
/// así que alcanza con anotar.
/// </summary>
internal sealed class RecordingSealer
{
    public List<(Guid ConnectionId, string FieldKey, string Plaintext)> Calls { get; } = [];

    public string KeyId { get; set; } = "k1";

    public ProtectedSecret Seal(Guid connectionId, string fieldKey, string plaintext)
    {
        Calls.Add((connectionId, fieldKey, plaintext));
        return new ProtectedSecret(KeyId, Encoding.UTF8.GetBytes($"{connectionId:D}|{fieldKey}|{plaintext}"));
    }

    public static string Open(ProtectedSecret secret) => Encoding.UTF8.GetString(secret.Ciphertext);
}
