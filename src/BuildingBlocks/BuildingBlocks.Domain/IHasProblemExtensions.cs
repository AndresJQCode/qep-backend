namespace BuildingBlocks.Domain;

/// <summary>
/// Un error que lleva datos extra para el cliente, hermana de <see cref="IHasFieldErrors"/>:
/// <c>ApiExceptionHandler</c> copia cada par en <c>ProblemDetails.Extensions</c>, sin pisar <c>code</c>,
/// <c>traceId</c> ni <c>errors</c>. Existe para que el 422 diga quién ya ocupa un documento
/// (<c>pos.customer.identification_taken</c> trae el <c>existing</c>) sin que la pantalla haga otra consulta.
/// </summary>
public interface IHasProblemExtensions
{
    IReadOnlyDictionary<string, object?> ProblemExtensions { get; }
}
