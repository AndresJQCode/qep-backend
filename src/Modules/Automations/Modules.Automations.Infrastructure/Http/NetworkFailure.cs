using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Modules.Automations.Application.Http;

namespace Modules.Automations.Infrastructure.Http;

/// <summary>
/// Spec 2026-11-03 D-2 y D-4: qué fue una falla de red del bloque HTTP y si se puede repetir. Todo sale de enums
/// (<see cref="HttpRequestError"/>, <see cref="SocketError"/>), nunca de <see cref="Exception.Message"/> ni de <c>ToString()</c>, que pueden
/// traer el host, la URL o la query (D-H5).
/// </summary>
internal static class NetworkFailure
{
    /// <summary>
    /// <paramref name="responded"/>: ya llegó el status. Entonces la petición llegó al servidor, aunque la excepción diga
    /// <c>ConnectionError</c>, y sólo un <c>GET</c> se puede repetir.
    /// </summary>
    internal static (string ErrorCode, bool Retryable) Classify(Exception exception, string method, bool responded)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var error = exception switch
        {
            HttpRequestException request => request.HttpRequestError,
            HttpIOException io => io.HttpRequestError,
            _ => (HttpRequestError?)null,
        };

        var retryable = error switch
        {
            // La petición no salió: no resolvió el host o el TLS no cerró. Repetir no duplica nada, tenga el método que tenga.
            HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError => !responded || IsGet(method),
            // Revisión del hotfix: ConnectionError sólo es «no salió» si lo causó el connect (un SocketException sin IOException en la
            // cadena). Con una IOException, o sin causa, pudo fallar al escribir el request o al leer los headers: sólo GET.
            HttpRequestError.ConnectionError => (!responded && IsConnectFailure(exception)) || IsGet(method),
            // Pudo llegar al servidor (P2-R37): sólo un GET. Unknown y una IOException sin HttpRequestError van por el lado seguro.
            HttpRequestError.ResponseEnded or HttpRequestError.Unknown or null => IsGet(method),
            // Deterministas: repetir da lo mismo.
            _ => false,
        };

        return (CodeOf(error, exception), retryable);
    }

    private static bool IsGet(string method) => string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);

    private static bool IsConnectFailure(Exception exception)
    {
        var socket = false;
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is IOException)
            {
                return false;
            }

            socket |= current is SocketException;
        }

        return socket;
    }

    // network:<falla>[:<socket>]. Si con el socket pasara el largo de la columna, va sin él: el recorte de HttpCallLog partiría el código.
    private static string CodeOf(HttpRequestError? error, Exception exception)
    {
        var code = $"{HttpCallOutcomes.Network}:{(error is { } known ? NameOf(known) : "io")}";
        if (SocketErrorIn(exception) is not { } socket)
        {
            return code;
        }

        var detailed = $"{code}:{NameOf(socket)}";
        return detailed.Length <= HttpCallLog.ErrorCodeMaxLength ? detailed : code;
    }

    private static SocketError? SocketErrorIn(Exception exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode;
            }
        }

        return null;
    }

    // Un valor que .NET agregue en el futuro sale con su nombre, o con su número si no tiene: nunca texto libre.
    private static string NameOf<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Enum.GetName(value) is { } name
            ? JsonNamingPolicy.SnakeCaseLower.ConvertName(name)
            : Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}
