using System.Diagnostics;
using System.Security.Claims;
using Bootstrapper.Authentication;
using Modules.Platform.Application;
using Modules.Platform.Domain;

namespace Api;

/// <summary>
/// Levanta la falla de un request y la manda al log de la aplicación.
///
/// Vive pegado a <see cref="ApiExceptionHandler"/> porque ése es el único punto por el que pasan
/// **todas** las fallas de **todos** los módulos: cualquier otro lugar sería una copia por módulo
/// que alguien va a olvidar en el próximo endpoint.
///
/// **Sólo métodos que escriben.** Un `GET` que falla se reintenta recargando la pantalla y no
/// deja nada a medias; una escritura que falla es la que hay que poder explicar después. Además,
/// los `GET` son la mayoría del tráfico: guardarlos convertiría el log en una copia del access
/// log y enterraría lo que importa.
/// </summary>
internal static class RequestFailureCapture
{
    private static readonly string[] MutatingMethods =
        [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    public static bool ShouldCapture(HttpContext httpContext) =>
        MutatingMethods.Contains(httpContext.Request.Method, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Arma la fila. No la guarda: quien llama decide cuándo, y el puerto es el que se traga sus
    /// propios errores.
    /// </summary>
    public static RequestFailure Describe(
        HttpContext httpContext,
        Exception exception,
        int statusCode,
        string errorCode,
        DateTimeOffset occurredAt) =>
        RequestFailure.Create(
            RequestFailureId.New(),
            SubjectIdOf(httpContext),
            httpContext.Request.Method,
            httpContext.Request.Path.Value ?? "/",
            statusCode,
            errorCode,
            exception.Message,
            // `ToString()` y no sólo el mensaje: trae el tipo, la pila y las internas. Un timeout
            // de `HttpClient` tiene un mensaje inútil ("A task was canceled") y una traza que dice
            // exactamente contra qué servicio se cayó.
            exception.ToString(),
            TraceIdOf(httpContext),
            occurredAt);

    /// <summary>
    /// La fila de una falla que no tuvo excepción: la que escriben la autenticación, la
    /// autorización, el enrutador o el limitador de tasa.
    ///
    /// No hay traza que guardar --nadie tiró-- y decirlo es mejor que dejar el campo vacío, que
    /// se lee como "se perdió". El código sintético existe para que estas filas se puedan filtrar
    /// igual que las demás: sin él, el desplegable de códigos no las alcanzaría.
    /// </summary>
    public static RequestFailure DescribeWithoutException(
        HttpContext httpContext,
        int statusCode,
        DateTimeOffset occurredAt) =>
        RequestFailure.Create(
            RequestFailureId.New(),
            SubjectIdOf(httpContext),
            httpContext.Request.Method,
            httpContext.Request.Path.Value ?? "/",
            statusCode,
            CodeForStatus(statusCode),
            DescriptionForStatus(statusCode),
            "La respuesta la produjo la tuberia de la API (autenticacion, autorizacion, " +
            "enrutamiento o limite de tasa), no un error de la aplicacion: no hay excepcion " +
            "ni traza que guardar.",
            TraceIdOf(httpContext),
            occurredAt);

    /// <summary>
    /// El identificador del request, **exactamente el mismo que recibe el cliente**.
    ///
    /// Tiene que ser esta cuenta y no <c>HttpContext.TraceIdentifier</c> a secas. La respuesta de
    /// error la escribe <c>ProblemDetailsService</c>, y su escritor por defecto pone en la
    /// extension `traceId` el id de la <see cref="Activity"/> en curso --formato W3C,
    /// `00-{trace}-{span}-01`-- y solo cae al de Kestrel (`0HNOE...:00000001`) si no hay
    /// actividad. Guardar el de Kestrel dejaba las dos puntas con identificadores distintos: el
    /// enlace "ver el detalle en el log" viajaba con uno y la fila estaba guardada con el otro,
    /// asi que el filtro no encontraba nada. Se copia la regla del framework para que coincidan
    /// gane quien gane.
    /// </summary>
    public static string TraceIdOf(HttpContext httpContext) =>
        Activity.Current?.Id ?? httpContext.TraceIdentifier;

    private static string CodeForStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status401Unauthorized => "request.unauthenticated",
        StatusCodes.Status403Forbidden => "request.forbidden",
        StatusCodes.Status404NotFound => "request.route_not_found",
        StatusCodes.Status405MethodNotAllowed => "request.method_not_allowed",
        StatusCodes.Status429TooManyRequests => "request.rate_limited",
        _ => "request.failed"
    };

    private static string DescriptionForStatus(int statusCode) => statusCode switch
    {
        StatusCodes.Status401Unauthorized =>
            "El request llego sin sesion valida.",
        StatusCodes.Status403Forbidden =>
            "El request fue rechazado por falta de permiso, por no coincidir el tenant activo " +
            "o por la defensa CSRF.",
        StatusCodes.Status404NotFound =>
            "Ninguna ruta de la API coincide con ese metodo y esa direccion.",
        StatusCodes.Status405MethodNotAllowed =>
            "La ruta existe pero no acepta ese metodo.",
        StatusCodes.Status429TooManyRequests =>
            "El request supero el limite de tasa.",
        _ => $"El request termino con {statusCode} sin excepcion."
    };

    /// <summary>
    /// El id interno de QEP, que es con el que se identifica a una persona en el resto del
    /// sistema. Null cuando la falla ocurrió antes de autenticar.
    /// </summary>
    private static Guid? SubjectIdOf(HttpContext httpContext) =>
        httpContext.User.FindFirstValue(QepClaimTypes.QepSubject) is { } subject &&
        Guid.TryParse(subject, out var subjectId)
            ? subjectId
            : null;
}
