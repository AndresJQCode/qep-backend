namespace Modules.Messaging.Application;

/// <summary>Spec 2026-10-14 §6.2: de qué producto de Meta viene una entrega. Decide el secreto que firma y el parser.</summary>
public enum WebhookProvider
{
    WhatsApp,
    Instagram,
}

/// <summary>Spec 2026-10-09 §8.2 y §11. La implementación lee <c>Meta:App</c> (y <c>Meta:Instagram</c> para el webhook de
/// Instagram) en Infrastructure. Sin la sección, <see cref="IsConfigured"/> es falso y nada verifica.</summary>
public interface IWebhookSignatureVerifier
{
    bool IsConfigured { get; }

    /// <summary><c>X-Hub-Signature-256 = sha256=&lt;hex&gt;</c>: HMAC-SHA256 del cuerpo crudo, comparado en
    /// tiempo constante.</summary>
    bool VerifyBody(ReadOnlySpan<byte> body, string? signatureHeader);

    /// <summary>Spec 2026-10-14 §6.2 (V-1): el webhook de Instagram se firma con el secreto del caso de uso Instagram; se acepta
    /// también el de la app por si Meta firmara con ese.</summary>
    bool VerifyBody(WebhookProvider provider, ReadOnlySpan<byte> body, string? signatureHeader);

    /// <summary><c>hub.verify_token</c> del GET, comparado en tiempo constante sobre los bytes UTF-8.</summary>
    bool VerifyToken(string? token);
}
