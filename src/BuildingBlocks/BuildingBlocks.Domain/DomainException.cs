namespace BuildingBlocks.Domain;

public abstract class DomainException : Exception
{
    protected DomainException(string code, string message)
        : base(message) => Code = code;

    /// <summary>
    /// Para cuando un error de dominio **envuelve** a otro: una falla de infraestructura que se
    /// traduce a un codigo que el cliente entiende. Sin esta sobrecarga, traducir significaba
    /// perder la excepcion original --su tipo y su traza-- justo en el caso donde es lo unico
    /// que explica que paso.
    ///
    /// No se puede encadenar con this(code, message): InnerException solo se fija en el
    /// constructor base, y por eso esta clase ya no usa constructor primario.
    /// </summary>
    protected DomainException(string code, string message, Exception innerException)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}
