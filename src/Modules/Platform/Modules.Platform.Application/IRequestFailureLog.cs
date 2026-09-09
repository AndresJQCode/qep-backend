using Modules.Platform.Domain;

namespace Modules.Platform.Application;

/// <summary>
/// Guarda una falla de request. Lo llama el manejador de excepciones de la API, que es el único
/// punto por donde pasan todas las fallas de todos los módulos.
///
/// **Fuera de cualquier transacción de negocio, y con contexto propio.** Al momento de la falla,
/// el <c>DbContext</c> del módulo que se cayó puede tener cambios a medias que justamente no
/// deben commitearse; y si la fila viajara en esa transacción, se iría con ella al revertirse —
/// que es exactamente el registro que existe para explicar por qué no quedó nada.
/// </summary>
public interface IRequestFailureLog
{
    /// <summary>
    /// **No propaga sus propios errores.** Se la llama mientras se está escribiendo la respuesta
    /// de error al cliente: si esto tirara, convertiría una falla explicada en un 500 mudo, que
    /// es peor que no tener el registro. Un fallo acá se anota en el log de la aplicación y nada
    /// más.
    /// </summary>
    Task RecordAsync(RequestFailure failure, CancellationToken cancellationToken);
}
