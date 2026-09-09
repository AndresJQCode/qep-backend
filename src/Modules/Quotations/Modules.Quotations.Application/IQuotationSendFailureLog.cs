using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// Anota en la línea de tiempo de la cotización que un envío falló, **fuera** de la transacción
/// del request.
///
/// Es la única escritura del módulo que no pasa por <see cref="IQuotationsUnitOfWork"/>, y tiene
/// que ser así. Cuando el envío falla, la transacción del request se descarta entera a propósito:
/// la cotización tiene que quedar en borrador, porque "Enviar" significa que de verdad llegó.
/// Guardar la entrada por el camino normal la ataría a esa misma transacción y se iría con ella
/// — justo la entrada que existe para explicar por qué no se guardó nada.
///
/// El <c>DbContext</c> del request tampoco sirve por otro motivo: al momento de la falla puede
/// tener cambios a medias del envío --el PDF regenerado, por ejemplo-- y un
/// <c>SaveChangesAsync</c> sobre él commitearía eso también. El adaptador abre un contexto propio.
///
/// **Acá va sólo la cara amigable**: qué pasó, en español y sin detalle técnico. La excepción
/// entera la guarda el log de fallas de la API (<c>audit.request_failures</c>), que captura
/// cualquier request que se cae y no sólo los envíos.
/// </summary>
public interface IQuotationSendFailureLog
{
    /// <summary>
    /// **No propaga sus propios errores.** Quien la llama está en un <c>catch</c>, a punto de
    /// relanzar la falla real: si esto tirara, la reemplazaría por una peor —"no se pudo guardar
    /// el registro de que no se pudo enviar"— y perdería la original. Un fallo acá se registra en
    /// el log de la aplicación y nada más.
    /// </summary>
    Task RecordAsync(
        QuotationHistoryEntry historyEntry,
        CancellationToken cancellationToken);
}
