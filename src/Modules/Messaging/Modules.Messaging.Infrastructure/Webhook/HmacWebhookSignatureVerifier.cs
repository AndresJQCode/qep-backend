using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Singleton: lee las opciones una vez. Nunca registra nada.</summary>
internal sealed class HmacWebhookSignatureVerifier(IOptions<MessagingMetaOptions> options) : IWebhookSignatureVerifier
{
    private const string Prefix = "sha256=";
    private const int DigestLength = 32;

    private readonly byte[]? _secret = Bytes(options.Value.AppSecret);
    private readonly byte[]? _token = Bytes(options.Value.WebhookVerifyToken);

    public bool IsConfigured => _secret is not null && _token is not null;

    public bool VerifyBody(ReadOnlySpan<byte> body, string? signatureHeader)
    {
        if (_secret is null || signatureHeader is null || !signatureHeader.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[DigestLength];
        Span<byte> provided = stackalloc byte[DigestLength];
        // Exactamente 64 dígitos hex: ni más corto ni con sobrantes.
        var hex = signatureHeader.AsSpan(Prefix.Length);
        if (hex.Length != DigestLength * 2
            || Convert.FromHexString(hex, provided, out _, out var written) != OperationStatus.Done
            || written != DigestLength)
        {
            return false;
        }

        HMACSHA256.HashData(_secret, body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    public bool VerifyToken(string? token)
    {
        if (_token is null || token is null)
        {
            return false;
        }

        var provided = Encoding.UTF8.GetBytes(token);
        // FixedTimeEquals exige el mismo largo; con largos distintos la respuesta es "no" sin medir nada.
        return provided.Length == _token.Length && CryptographicOperations.FixedTimeEquals(_token, provided);
    }

    private static byte[]? Bytes(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Encoding.UTF8.GetBytes(value.Trim());
}
