using Modules.Buyers.Application;
using Modules.Store.Application;

namespace Bootstrapper;

/// <summary>
/// Store → Buyers: el desafío Turnstile de la tienda. <c>false</c> si no se pasó; un Cloudflare caído lanza el 503
/// <c>store.captcha.unavailable</c> del verificador, sin cambios.
/// </summary>
internal sealed class BuyerCaptcha(ICaptchaVerifier verifier) : IBuyerCaptcha
{
    public Task<bool> VerifyAsync(string token, string visitorIp, string host, CancellationToken cancellationToken) =>
        verifier.VerifyAsync(token, visitorIp, host, cancellationToken);
}
