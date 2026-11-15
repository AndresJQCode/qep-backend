namespace Modules.Support.Application;

/// <summary>
/// El desafío anti-bot del formulario público (plan F28): el Turnstile de la tienda, con adaptador en Bootstrapper sobre el
/// verificador de Store. El hostname esperado lo pone el adaptador (<see cref="ISupportPublicOptions.Hostname"/>). <c>false</c> si no se
/// pasó; un Cloudflare caído no es <c>false</c>: lanza el 503 del verificador, para que la persona reintente.
/// </summary>
public interface ISupportCaptcha
{
    Task<bool> VerifyAsync(string token, string visitorIp, CancellationToken cancellationToken);
}
