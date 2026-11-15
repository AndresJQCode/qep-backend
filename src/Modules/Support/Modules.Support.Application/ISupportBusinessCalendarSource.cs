using Modules.Support.Domain;

namespace Modules.Support.Application;

/// <summary>El calendario y la zona del tenant con que cuenta el plazo de sus tickets (spec §7.2). El adaptador vive en Bootstrapper:
/// arma el calendario de lunes a viernes con el país del tenant (festivos de Colombia si es <c>CO</c>), y Soporte nunca referencia
/// Mensajería.</summary>
public interface ISupportBusinessCalendarSource
{
    /// <summary><paramref name="timeZone"/> es la del reloj del tenant que quien llama ya leyó: así el reloj se pide una sola vez por
    /// petición (revisión final, B5).</summary>
    Task<SupportClockContext> GetAsync(Guid tenantId, TimeZoneInfo timeZone, CancellationToken cancellationToken);
}
