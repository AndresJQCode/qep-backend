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
