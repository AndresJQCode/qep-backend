using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.Options;

namespace Modules.Messaging.Infrastructure.Webhook;

/// <summary>Singleton: lee las opciones una vez. Nunca registra nada. Spec 2026-10-14 §6.2 (V-1): el webhook de Instagram se
/// firma con el secreto del caso de uso Instagram (<c>Meta:Instagram:AppSecret</c>); se acepta también el de la app
/// (<c>Meta:App:AppSecret</c>), por si Meta firmara con ése. El de WhatsApp sigue aceptando sólo el de la app.</summary>
internal sealed class HmacWebhookSignatureVerifier(IOptions<MessagingMetaOptions> options, IOptions<MessagingInstagramOptions> instagram)
    : IWebhookSignatureVerifier
{
    private const string Prefix = "sha256=";
    private const int DigestLength = 32;

    private readonly byte[]? _secret = Bytes(options.Value.AppSecret);
    private readonly byte[]? _instagramSecret = Bytes(instagram.Value.AppSecret);
    private readonly byte[]? _token = Bytes(options.Value.WebhookVerifyToken);

    /// <summary>Compatibilidad con quien lo arma sólo con <c>Meta:App</c> (pruebas unitarias): sin secreto de Instagram.</summary>
    public HmacWebhookSignatureVerifier(IOptions<MessagingMetaOptions> options)
        : this(options, Microsoft.Extensions.Options.Options.Create(new MessagingInstagramOptions()))
    {
    }

    public bool IsConfigured => _secret is not null && _token is not null;

    public bool VerifyBody(ReadOnlySpan<byte> body, string? signatureHeader) => VerifyWith(_secret, body, signatureHeader);

    public bool VerifyBody(WebhookProvider provider, ReadOnlySpan<byte> body, string? signatureHeader) =>
        provider == WebhookProvider.Instagram
            ? VerifyWith(_instagramSecret, body, signatureHeader) || VerifyWith(_secret, body, signatureHeader)
            : VerifyWith(_secret, body, signatureHeader);

    private static bool VerifyWith(byte[]? secret, ReadOnlySpan<byte> body, string? signatureHeader)
    {
        if (secret is null || signatureHeader is null || !signatureHeader.StartsWith(Prefix, StringComparison.Ordinal))
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

        HMACSHA256.HashData(secret, body, expected);
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
