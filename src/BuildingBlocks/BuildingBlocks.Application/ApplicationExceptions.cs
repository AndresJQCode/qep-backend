namespace BuildingBlocks.Application;

public sealed class ResourceNotFoundException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class RequestForbiddenException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Distinct from RequestForbiddenException (403): signals a failed authentication,
// not a denied authorization. Used by webhook endpoints whose authenticity rests on
// an HMAC signature — Shopify's mandatory compliance webhooks require a 401 (not 403)
// when the signature is invalid or the caller cannot be authenticated.
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
