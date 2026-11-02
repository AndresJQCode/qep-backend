using BuildingBlocks.Domain;

namespace Modules.Wms.Domain;

/// <summary>Regla de negocio de Bodegas: <c>ApiExceptionHandler</c> la responde 422 con su código.</summary>
public sealed class WmsDomainException : DomainException
{
    public WmsDomainException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>Cuando el 422 envuelve una falla de la base (un índice único traducido en <c>WmsUnitOfWork</c>): la causa queda
    /// en <c>platform.request_failures</c>.</summary>
    public WmsDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException)
    {
    }
}
