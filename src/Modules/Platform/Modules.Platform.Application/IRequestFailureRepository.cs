using Modules.Platform.Domain;

namespace Modules.Platform.Application;

/// <summary>
/// La lectura y el purgado del log de fallas. Separado de <see cref="IRequestFailureLog"/> a
/// propósito: ese lo llama el manejador de excepciones con un contexto propio y no puede fallar;
/// éste lo llaman dos casos de uso normales, con la unidad de trabajo del módulo.
///
/// Todo método recibe <c>tenantId</c> primero, igual que el resto de los repositorios: el filtro
/// de tenant es parte de la consulta, nunca un argumento que el llamador se pueda olvidar.
/// </summary>
public interface IRequestFailureRepository
{
    /// <summary>
    /// Una página del log y el total que la acompaña. Cada filtro es opcional y sólo se aplica
    /// cuando llega distinto de null; los que llegan se combinan con AND.
    /// </summary>
    Task<(IReadOnlyList<RequestFailure> Items, int Total)> SearchAsync(
        Guid tenantId,
        string? moduleName,
        string? errorCode,
        DateTimeOffset? occurredFrom,
        DateTimeOffset? occurredTo,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Los módulos y los códigos de error que **este tenant tiene realmente** en su log, para
    /// llenar los dos desplegables del filtro.
    ///
    /// Salen de la tabla y no de una lista fija en el código por dos motivos: el código de error
    /// lo puede inventar cualquier módulo nuevo sin que esta pantalla se entere, y ofrecer para
    /// filtrar un valor que no tiene ni una fila lleva a una tabla vacía sin explicación.
    /// </summary>
    Task<(IReadOnlyList<string> Modules, IReadOnlyList<string> ErrorCodes)> ListFilterValuesAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Borra las filas del tenant anteriores a <paramref name="olderThan"/> y devuelve cuántas
    /// borró.
    ///
    /// Borrado real y no lógico: es un log operativo, no auditoría. La auditoría —la que puede
    /// tener que responder por qué alguien hizo algo— es <c>audit.entries</c> y ésa es
    /// append-only y no se toca desde ningún endpoint.
    ///
    /// Acotado al tenant como todo lo demás: un administrador purga su espacio de trabajo, no el
    /// de al lado.
    /// </summary>
    Task<int> PurgeOlderThanAsync(
        Guid tenantId,
        DateTimeOffset olderThan,
        CancellationToken cancellationToken);
}
