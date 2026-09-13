using Modules.Quotations.Domain;

namespace Modules.Quotations.Application;

/// <summary>
/// La cola de exportaciones (D2). Puerto y no la tabla directa para que pasar a un broker, si
/// algún día hace falta, sea cambiar el adaptador y no los casos de uso.
/// </summary>
// CA1711: el analizador asume que "Queue" implica heredar de System.Collections.Queue; acá es
// sólo el sustantivo del dominio (D2/D6) y el nombre lo fija el spec — falso positivo.
#pragma warning disable CA1711
public interface IExportJobQueue
#pragma warning restore CA1711
{
    /// <summary>Lo suma a la unidad de trabajo; se persiste con
    /// <see cref="IQuotationsUnitOfWork.SaveChangesAsync"/>.</summary>
    void Add(ExportJob job);

    /// <summary>Los Pending y Processing de esa persona en ese tenant, de los dos tipos.</summary>
    Task<int> CountPendingAsync(Guid tenantId, Guid requestedBy, CancellationToken cancellationToken);

    /// <summary>
    /// Toma exclusiva (D6): un Pending vencido o un Processing con el lease vencido, con un
    /// intento más y lease nuevo. Commitea por su cuenta —el lease tiene que verse desde otros
    /// procesos ya— y deja el job trackeado para que cerrarlo sea un guardado de la unidad de
    /// trabajo. <c>null</c> si no hay nada que tomar.
    /// </summary>
    Task<ExportJob?> ClaimNextAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>D13: borra los Completed y Failed que terminaron antes de <paramref name="cutoff"/>.</summary>
    Task<int> PurgeFinishedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
