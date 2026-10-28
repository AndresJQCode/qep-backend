using BuildingBlocks.Domain;

namespace Modules.Messaging.Domain;

/// <summary>Regla de negocio de Messaging: <c>ApiExceptionHandler</c> la responde 422 con su código.</summary>
public sealed class MessagingDomainException : DomainException
{
    public MessagingDomainException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>Cuando el 422 envuelve una falla de infraestructura (p. ej., Integrations no pudo pasar la conexión a
    /// <c>NeedsAttention</c>): la causa queda en <c>platform.request_failures</c>.</summary>
    public MessagingDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException)
    {
    }
}
