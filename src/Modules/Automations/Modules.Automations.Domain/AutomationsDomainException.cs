using BuildingBlocks.Domain;

namespace Modules.Automations.Domain;

/// <summary>Regla de negocio de los chatbots: <c>ApiExceptionHandler</c> la responde 422 con su código.</summary>
public sealed class AutomationsDomainException : DomainException
{
    public AutomationsDomainException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>Cuando el 422 envuelve una falla de la base: la causa queda en <c>platform.request_failures</c>.</summary>
    public AutomationsDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException)
    {
    }
}
