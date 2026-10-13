using BuildingBlocks.Domain;

namespace Modules.Pipeline.Domain;

/// <summary>Regla de negocio del embudo: <c>ApiExceptionHandler</c> la responde 422 con su código.</summary>
public sealed class PipelineDomainException : DomainException
{
    public PipelineDomainException(string code, string message)
        : base(code, message)
    {
    }

    /// <summary>Cuando el 422 envuelve una falla de la base (la FK de etapa en <c>PipelineUnitOfWork</c>): la causa queda en
    /// <c>platform.request_failures</c>.</summary>
    public PipelineDomainException(string code, string message, Exception innerException)
        : base(code, message, innerException)
    {
    }
}
