namespace BuildingBlocks.Application;

public sealed class ResourceNotFoundException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class RequestForbiddenException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Distinta de RequestForbiddenException (403): señala una autenticación fallida,
// no una autorización denegada. La usan los endpoints de webhook cuya autenticidad se
// apoya en una firma HMAC — los webhooks de cumplimiento obligatorios de Shopify exigen
// un 401 (no un 403) cuando la firma es inválida o no se puede autenticar al llamador.
public sealed class RequestUnauthorizedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class RequestConcurrencyException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

// 409: lo pedido choca con lo que ya existe y el cliente puede resolverlo cambiando el pedido (un nombre repetido,
// una campaña que ya no es borrador). Distinta de RequestConcurrencyException (412), que es «el recurso cambió desde
// que lo leíste», y de DomainException (422), que es «el pedido no cumple una regla de negocio».
public sealed class ResourceConflictException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class PreconditionRequiredException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// 503: lo pedido es válido pero el servidor no puede cumplirlo por su configuración (spec 2026-10-08:
// sin llave activa, Integrations no puede cifrar). Distinta de un 500: no es un error del código, y
// el cliente puede mostrar "todavía no disponible" en vez de "algo falló".
public sealed class ServiceUnavailableException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// 502: un servicio externo respondió algo que no reconocemos, falló o no respondió (spec 2026-10-26 §3.3: Meta al bloquear un
// contacto). Distinta de un 500: no es un error del código, y el cliente puede ofrecer reintentar en vez de "algo falló".
public sealed class UpstreamServiceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// 429: el llamador superó su cupo de la API externa. RetryAfter, si se conoce, viaja en el header Retry-After.
public sealed class RequestRateLimitedException(string code, string message, TimeSpan? retryAfter) : Exception(message)
{
    public string Code { get; } = code;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
