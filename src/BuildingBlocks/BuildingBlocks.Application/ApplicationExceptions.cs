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
