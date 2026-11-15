using Modules.Store.Application;
using Modules.Support.Application;

namespace Bootstrapper;

/// <summary>
/// Store → Support: el Turnstile de la tienda para el formulario público (plan F28, como <see cref="BuyerCaptcha"/>), con el widget y
/// el secreto de <c>Store:Turnstile:*</c>. El hostname esperado es <c>Support:PublicForm:Hostname</c>; el verificador lo exige sólo en
/// Production. <c>false</c> si no se pasó; un Cloudflare caído lanza el 503 <c>store.captcha.unavailable</c> del verificador, sin cambios.
/// </summary>
internal sealed class SupportCaptcha(ICaptchaVerifier verifier, ISupportPublicOptions options) : ISupportCaptcha
{
    public Task<bool> VerifyAsync(string token, string visitorIp, CancellationToken cancellationToken) =>
        verifier.VerifyAsync(token, visitorIp, options.Hostname, cancellationToken);
}
