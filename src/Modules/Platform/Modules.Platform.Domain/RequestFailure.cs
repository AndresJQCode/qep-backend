namespace Modules.Platform.Domain;

/// <summary>
/// Un request que se cayó, con el error entero. El log de la aplicación.
///
/// **Sólo métodos que escriben** (POST, PUT, PATCH, DELETE). Un GET que falla se reintenta
/// recargando y no deja nada a medias; una escritura que falla es la que hay que poder explicar
/// después, y es la que alguien viene a buscar cuando pregunta "¿por qué no se guardó?".
///
/// Vive en Audit y no en el módulo que falló, porque **no es de ningún módulo**: lo escribe el
/// manejador de excepciones de la API, que es el único punto por donde pasan todas las fallas de
/// todos los módulos. Audit ya es el dueño del registro transversal de qué pasó en la app; esto
/// es la otra mitad, la de lo que no pasó.
///
/// Es append-only, igual que <see cref="AuditEntry"/>: se crea y no se muta. Lo único que se
/// hace después es borrarlo en lote cuando envejece — a diferencia de la auditoría, este log es
/// operativo y se purga.
/// </summary>
public sealed class RequestFailure
{
    public const int MethodMaxLength = 10;

    public const int PathMaxLength = 500;

    /// <summary>El nombre del módulo sale de la ruta, así que su techo lo fija el segmento más
    /// largo que puede aparecer ahí.</summary>
    public const int ModuleMaxLength = 60;

    public const int ErrorCodeMaxLength = 120;

    public const int MessageMaxLength = 1000;

    /// <summary>
    /// El tope de la traza. Generoso a propósito: es el campo que existe para diagnosticar, y una
    /// cadena de excepciones anidadas con su pila pasa los 4000 sin ser rara. Más allá de esto la
    /// información deja de estar en el texto y pasa a estar en los logs de la aplicación, que se
    /// buscan por <see cref="TraceId"/>.
    /// </summary>
    public const int DetailMaxLength = 20000;

    public const int TraceIdMaxLength = 120;

    /// <summary>
    /// Lo que se muestra como módulo cuando la ruta no tiene la forma esperada. Una fila con un
    /// módulo desconocido sigue siendo útil —tiene el error, la ruta y la traza— y perderla por
    /// no poder clasificarla sería el peor canje posible.
    /// </summary>
    public const string UnknownModule = "desconocido";

    // EF Core materializa por acá.
    private RequestFailure()
    {
        Method = string.Empty;
        Path = string.Empty;
        Module = string.Empty;
        Message = string.Empty;
        Detail = string.Empty;
    }

    private RequestFailure(
        RequestFailureId id,
        Guid? tenantId,
        Guid? subjectId,
        string method,
        string path,
        string module,
        int statusCode,
        string? errorCode,
        string message,
        string detail,
        string? traceId,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TenantId = tenantId;
        SubjectId = subjectId;
        Method = method;
        Path = path;
        Module = module;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Message = message;
        Detail = detail;
        TraceId = traceId;
        OccurredAt = occurredAt;
    }

    public RequestFailureId Id { get; private init; }

    /// <summary>
    /// Nullable: hay endpoints que fallan antes de que haya un tenant —el login, el alta de un
    /// tenant nuevo—. El reporte filtra por tenant, así que esas filas sólo se ven en los logs de
    /// la aplicación; es el canje correcto, porque mostrarlas en algún tenant sería mostrárselas
    /// a alguien que no tiene nada que ver.
    /// </summary>
    public Guid? TenantId { get; private init; }

    /// <summary>Quién llamaba. Null cuando la falla fue antes de autenticar.</summary>
    public Guid? SubjectId { get; private init; }

    public string Method { get; private init; }

    public string Path { get; private init; }

    /// <summary>
    /// Qué parte de la app falló, derivado de la ruta por <see cref="ModuleFor"/>. Se guarda
    /// resuelto y no se calcula al leer: es una columna por la que se filtra, y hacerlo con una
    /// función sobre cada fila obliga a recorrer la tabla entera.
    /// </summary>
    public string Module { get; private init; }

    public int StatusCode { get; private init; }

    /// <summary>
    /// El código que la API devolvió en el cuerpo (`catalog.product.not_found`,
    /// `validation.failed`, `server.unexpected`). Es el criterio por el que se agrupa: una misma
    /// causa aparece con el mismo código aunque la ruta cambie.
    /// </summary>
    public string? ErrorCode { get; private init; }

    /// <summary>El mensaje de la excepción, sin la pila. Es lo que se lee en la tabla.</summary>
    public string Message { get; private init; }

    /// <summary>
    /// La excepción entera: tipo, mensaje, traza de pila y todas las internas, tal como las
    /// imprime <c>ToString()</c>. Es lo que se abre en el detalle.
    /// </summary>
    public string Detail { get; private init; }

    /// <summary>El identificador que la respuesta devolvió como <c>traceId</c>. Es lo que conecta
    /// esta fila con las líneas del log de la aplicación y con la traza distribuida.</summary>
    public string? TraceId { get; private init; }

    public DateTimeOffset OccurredAt { get; private init; }

    /// <param name="path">De acá salen el módulo **y** el tenant: los dos se derivan de la ruta,
    /// que es el único dato que sobrevive a los dos caminos de captura. Ver
    /// <see cref="TenantIdFor"/>.</param>
    public static RequestFailure Create(
        RequestFailureId id,
        Guid? subjectId,
        string method,
        string path,
        int statusCode,
        string? errorCode,
        string message,
        string detail,
        string? traceId,
        DateTimeOffset occurredAt) =>
        new(
            id,
            TenantIdFor(path),
            subjectId,
            Truncate(method, MethodMaxLength) ?? "?",
            Truncate(path, PathMaxLength) ?? "/",
            ModuleFor(path),
            statusCode,
            Truncate(errorCode, ErrorCodeMaxLength),
            // Una excepción sin mensaje es rara pero posible, y la columna no admite null: el
            // registro vale igual por su traza y su código.
            Truncate(message, MessageMaxLength) ?? "(sin mensaje)",
            Truncate(detail, DetailMaxLength) ?? "(sin detalle)",
            Truncate(traceId, TraceIdMaxLength),
            occurredAt);

    /// <summary>
    /// Qué parte de la app es una ruta.
    ///
    /// Todas las rutas de la API tienen una de dos formas:
    /// <c>/api/v1/tenants/{tenantId}/&lt;módulo&gt;/…</c> para lo que vive dentro de un espacio de
    /// trabajo, y <c>/api/v1/&lt;módulo&gt;/…</c> para lo que no (autenticación, registro, roles).
    /// El tenant se saltea porque es el mismo para todas las filas del reporte y como módulo no
    /// distinguiría nada.
    ///
    /// Puro y estático a propósito, como <c>ProductPriceChangeDetector</c>: se prueba entero en
    /// memoria, sin levantar la app ni armar un <c>HttpContext</c>.
    /// </summary>
    public static string ModuleFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return UnknownModule;
        }

        // Sin la query string y sin la barra final: `/catalog/products?page=2` y
        // `/catalog/products/` son la misma ruta para esto.
        var withoutQuery = path.Split('?', 2)[0];
        var segments = withoutQuery.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // `api` y la versión. Una ruta que no las tenga no es de la API.
        if (segments.Length < 3 ||
            !string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase))
        {
            return UnknownModule;
        }

        var afterVersion = segments[2];
        if (!string.Equals(afterVersion, "tenants", StringComparison.OrdinalIgnoreCase))
        {
            return afterVersion.ToLowerInvariant();
        }

        // `/api/v1/tenants/{tenantId}/<módulo>`. Sin el segmento del módulo —una ruta que opera
        // sobre el tenant mismo— el módulo es el propio `tenants`.
        return segments.Length >= 5
            ? segments[4].ToLowerInvariant()
            : "tenants";
    }

    /// <summary>
    /// El tenant al que iba dirigido el request, sacado de la ruta
    /// (<c>/api/v1/tenants/{tenantId}/…</c>).
    ///
    /// **De la ruta y no de los route values de ASP.NET.** Esa fue la primera versión y estaba
    /// mal de dos maneras: <c>ExceptionHandlerMiddleware</c> limpia los route values antes de
    /// invocar al manejador de errores, así que toda falla con excepción --las que importan--
    /// quedaba sin tenant; y un 404 no llega a poblarlos nunca, porque no hay endpoint que los
    /// llene. El resultado era un log que existía y que el reporte, que filtra por tenant, no
    /// podía mostrar. La ruta, en cambio, sobrevive a las dos cosas.
    ///
    /// El segmento lo escribe el cliente, así que en un request sin autenticar es un dato que él
    /// controla: alguien puede provocar 401 contra la ruta de otro tenant y ensuciarle el log.
    /// Se acepta a conciencia — es ruido, no filtración, y el canje al revés sería peor: exigir
    /// el tenant autenticado dejaría fuera del log justamente los 401, que son el caso que más
    /// se consulta ("se me vencio la sesión y no guardó").
    /// </summary>
    public static Guid? TenantIdFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var segments = path.Split('?', 2)[0]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        // `/api/v1/tenants/{tenantId}`: el id es el cuarto segmento.
        return segments.Length >= 4 &&
            string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[2], "tenants", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(segments[3], out var tenantId)
                ? tenantId
                : null;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
