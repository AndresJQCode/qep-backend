using System.Net;
using System.Net.Sockets;

namespace Modules.Integrations.Infrastructure.ExternalApi;

/// <summary>
/// Spec 2026-10-21 D-H2: la API de un tenant no puede usar al backend como proxy hacia la red interna. La URL se juzga dos
/// veces: al guardarla (<see cref="Reject"/>: <c>https</c> y un nombre, nunca una IP literal) y en cada conexión
/// (<see cref="CreateHandler"/>), porque el DNS de un nombre público puede cambiar después de guardado (DNS rebinding). Lo
/// usan el probador de la conexión y el bloque HTTP del chatbot.
/// </summary>
public static class ExternalApiUrlPolicy
{
    /// <summary><c>null</c> si la URL se acepta; si no, el motivo técnico (nunca sale por HTTP).</summary>
    public static string? Reject(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            return "host_missing";
        }

        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return "scheme_not_https";
        }

        if (string.IsNullOrEmpty(url.Host))
        {
            return "host_missing";
        }

        return url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 ? "host_is_ip_literal" : null;
    }

    /// <summary>
    /// Fallo P2-R28: todo lo que no es internet público. Una IPv4 mapeada en IPv6 (<c>::ffff:0:0/96</c>) o traducida por
    /// NAT64 (<c>64:ff9b::/96</c>) se desenvuelve y se juzga como IPv4: si no, <c>::ffff:127.0.0.1</c> llegaría al loopback.
    /// </summary>
    public static bool IsForbidden(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return IsForbiddenV4(address.MapToIPv4().GetAddressBytes());
            }

            var bytes = address.GetAddressBytes();
            if (IsNat64(bytes))
            {
                return IsForbiddenV4(bytes[12..]);
            }

            return address.Equals(IPAddress.IPv6Any)
                || address.Equals(IPAddress.IPv6Loopback)
                || address.IsIPv6LinkLocal // fe80::/10
                || address.IsIPv6SiteLocal // fec0::/10, obsoleto pero enrutable dentro de una red
                || address.IsIPv6UniqueLocal // fc00::/7
                || address.IsIPv6Multicast; // ff00::/8
        }

        return IsForbiddenV4(address.GetAddressBytes());
    }

    /// <summary>
    /// El handler primario de toda llamada a una API externa. Sin redirecciones (un 3xx llevaría el header de la clave, que
    /// <c>SocketsHttpHandler</c> no quita, al host de <c>Location</c>) y sin proxy (con proxy, <c>ConnectCallback</c> vería
    /// el proxy y no el destino, P2-R28). <c>ConnectCallback</c> resuelve el host y, si alguna dirección es prohibida, no
    /// conecta a ninguna: lanza <see cref="ExternalApiUrlRejectedException"/>, que llega envuelta en un
    /// <see cref="HttpRequestException"/>.
    /// </summary>
    /// <param name="resolve">El DNS; <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> por defecto. Las
    /// pruebas pasan uno de mentira.</param>
    public static SocketsHttpHandler CreateHandler(Func<string, CancellationToken, ValueTask<IPAddress[]>>? resolve = null)
    {
        resolve ??= static (host, cancellationToken) => new ValueTask<IPAddress[]>(Dns.GetHostAddressesAsync(host, cancellationToken));
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var addresses = await resolve(context.DnsEndPoint.Host, cancellationToken);
                if (addresses.Length == 0)
                {
                    throw new SocketException((int)SocketError.HostNotFound);
                }

                if (addresses.Any(IsForbidden))
                {
                    throw new ExternalApiUrlRejectedException("resolved_to_forbidden_range");
                }

                return await ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            },
        };
    }

    // En orden, a la primera que conecte: un host con AAAA primero no puede quedar inalcanzable desde un cluster sin salida
    // IPv6. Todas ya pasaron por IsForbidden: se conecta a la dirección juzgada, no se vuelve a resolver.
    private static async ValueTask<Stream> ConnectAsync(IPAddress[] addresses, int port, CancellationToken cancellationToken)
    {
        SocketException? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw last!;
    }

    private static bool IsNat64(byte[] bytes) =>
        bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
        && bytes[4..12].All(value => value == 0);

    private static bool IsForbiddenV4(byte[] bytes) =>
        bytes[0] switch
        {
            0 => true, // 0.0.0.0/8
            10 => true, // 10/8
            100 => bytes[1] is >= 64 and <= 127, // 100.64.0.0/10, CGNAT
            127 => true, // loopback
            169 => bytes[1] == 254, // link-local, incluida la metadata 169.254.169.254
            172 => bytes[1] is >= 16 and <= 31, // 172.16/12
            192 => (bytes[1] == 168) || (bytes[1] == 0 && bytes[2] == 0), // 192.168/16, 192.0.0.0/24
            198 => bytes[1] is 18 or 19, // 198.18.0.0/15, pruebas de red
            >= 224 => true, // multicast 224/4 y reservado 240/4, con 255.255.255.255
            _ => false,
        };
}

/// <summary>La URL o lo que su host resolvió no se acepta (D-H2). <see cref="Reason"/> es técnico y nunca sale por HTTP.</summary>
public sealed class ExternalApiUrlRejectedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
