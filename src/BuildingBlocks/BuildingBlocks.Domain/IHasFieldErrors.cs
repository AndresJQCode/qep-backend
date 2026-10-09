namespace BuildingBlocks.Domain;

/// <summary>
/// Un error que sabe a qué campos del formulario se refiere. <c>ApiExceptionHandler</c> publica
/// <see cref="FieldErrors"/> como <c>errors</c>, con la misma forma que el 422 de FluentValidation,
/// que es el único mapa que el formulario sabe leer para marcar un input (spec 2026-10-08:
/// <c>credentials_rejected</c> marca <c>secrets.apiToken</c>). Las claves son las del cuerpo; los
/// mensajes nunca llevan el valor que se mandó.
/// </summary>
public interface IHasFieldErrors
{
    IReadOnlyDictionary<string, string[]> FieldErrors { get; }
}
