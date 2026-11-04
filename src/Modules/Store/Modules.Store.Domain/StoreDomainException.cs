using BuildingBlocks.Domain;

namespace Modules.Store.Domain;

public sealed class StoreDomainException : DomainException
{
    public StoreDomainException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>Cuando el error de Store traduce otro de un módulo vecino: la excepción original se conserva para el diagnóstico.</summary>
    public StoreDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException)
    {
    }
}
