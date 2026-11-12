namespace Modules.Store.Application;

/// <summary>
/// El desafío anti-bot del envío de solicitudes (Cloudflare Turnstile). <c>false</c> = el desafío no se pasó, o se resolvió en otro sitio
/// (el hostname no es el de la tienda). Un Cloudflare caído, lento o con 5xx NO es <c>false</c>: lanza
/// <c>ServiceUnavailableException("store.captcha.unavailable")</c>, para que el visitante reintente en vez de creer que es un bot.
/// </summary>
public interface ICaptchaVerifier
{
    Task<bool> VerifyAsync(string token, string remoteIp, string expectedHostname, CancellationToken cancellationToken);
}
