using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Modules.Store.Application;

namespace Modules.Store.Api;

/// <summary>
/// El servidor de la tienda pública (storefront) se autentica con un secreto compartido, no con la cookie de sesión: las rutas
/// públicas nunca leen la sesión. Solo con la clave válida se confía en <c>X-Qep-Visitor-Ip</c>; sin ella el header se ignora,
/// así que quien no tiene la clave no puede mudarse de cubeta del limitador mandando otra IP.
/// </summary>
public static class StorefrontClient
{
    public const string KeyHeader = "X-Qep-Storefront-Key";
    public const string VisitorIpHeader = "X-Qep-Visitor-Ip";

    /// <summary>
    /// El token de la sesión de comprador que reenvía el BFF. Copia de <c>PublicBuyerEndpoints.BuyerSessionHeader</c> (Store no ve
    /// Buyers); <c>BuyerParityTests</c> (Bootstrapper.UnitTests) los ata.
    /// </summary>
    public const string BuyerSessionHeader = "X-Qep-Buyer-Session";

    /// <summary>Donde el filtro deja la IP del visitante para que el handler del envío la use como IP del consentimiento.</summary>
    public const string VisitorIpItem = "store.visitor_ip";

    /// <summary>
    /// Compara en tiempo constante sobre los bytes UTF-8. Un secreto vacío o de solo espacios falla CERRADO: ninguna llamada
    /// pasa, y un header vacío tampoco, así que "" nunca es igual a "".
    /// </summary>
    public static bool TryAuthenticate(HttpContext context, string? sharedSecret)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(sharedSecret))
        {
            return false;
        }

        var presented = context.Request.Headers[KeyHeader];
        if (presented.Count != 1 || string.IsNullOrWhiteSpace(presented[0]))
        {
            return false;
        }

        // FixedTimeEquals devuelve false de inmediato si los largos difieren: lo único que se filtra es el largo del secreto.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented[0]!),
            Encoding.UTF8.GetBytes(sharedSecret));
    }

    /// <summary>La IP del visitante que declara el storefront, solo con la clave válida y un valor que sea una IP.</summary>
    public static bool TryGetVisitorIp(HttpContext context, string? sharedSecret, out string visitorIp)
    {
        ArgumentNullException.ThrowIfNull(context);
        visitorIp = string.Empty;
        if (!TryAuthenticate(context, sharedSecret))
        {
            return false;
        }

        var declared = context.Request.Headers[VisitorIpHeader];
        if (declared.Count != 1
            || declared[0] is not { Length: > 0 and <= 45 } text
            || !IPAddress.TryParse(text.Trim(), out var address))
        {
            return false;
        }

        visitorIp = address.ToString();
        return true;
    }

    /// <summary>
    /// Como <see cref="TryGetVisitorIp"/>, con el secreto de <see cref="StoreOptions"/>: para quien no recibe el secreto por parámetro
    /// (los módulos que cuelgan de este prefijo, como Buyers).
    /// </summary>
    public static bool TryGetDeclaredVisitorIp(HttpContext context, out string visitorIp)
    {
        ArgumentNullException.ThrowIfNull(context);
        var secret = context.RequestServices.GetRequiredService<IOptions<StoreOptions>>().Value.Storefront.SharedSecret;
        return TryGetVisitorIp(context, secret, out visitorIp);
    }

    /// <summary>
    /// La partición del limitador y la IP del consentimiento: la declarada si es confiable y, si no, la del par directo (que
    /// <c>UseForwardedHeaders</c> ya reemplazó por la del cliente cuando corresponde).
    /// </summary>
    public static string VisitorIp(HttpContext context, string? sharedSecret) =>
        TryGetVisitorIp(context, sharedSecret, out var visitorIp)
            ? visitorIp
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
