using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>El calendario y la zona del tenant con que cuenta el plazo de sus tickets (spec §7.2). El adaptador vive en Bootstrapper:
/// arma el calendario con el horario predeterminado de Mensajería y el país del tenant, y Soporte nunca referencia Mensajería.</summary>
public interface ISupportBusinessCalendarSource
{
    Task<SupportClockContext> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}
