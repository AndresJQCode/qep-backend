namespace Bootstrapper.ReverseProxy;

/// <summary>
/// Las redes desde las que se confía en <c>X-Real-IP</c> y <c>X-Forwarded-Proto</c>. Sólo si el par
/// directo del pod cae en una de ellas, <c>RemoteIpAddress</c> pasa a ser la IP del cliente que
/// nginx anotó en <c>X-Real-IP</c>; de cualquier otro par el encabezado se ignora, porque lo puede
/// escribir cualquiera. <c>X-Forwarded-For</c> no se usa nunca: su entrada de más a la derecha es
/// el borde de Cloudflare, no el cliente (ver <c>AddQepForwardedHeaders</c>).
///
/// Sin valor por defecto a propósito: la topología del clúster es del ambiente, no del código. Sin
/// la clave sólo queda el loopback que el framework ya trae, así que el desarrollo local y las
/// pruebas se comportan como antes.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Redes en notación CIDR, p. ej. <c>10.50.0.0/24</c>.</summary>
    public List<string> KnownNetworks { get; set; } = [];
}
