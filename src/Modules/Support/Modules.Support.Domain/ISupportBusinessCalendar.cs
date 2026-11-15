namespace Modules.Support.Domain;

/// <summary>Spec 2026-11-14 §7.2 y §13.4 [owner 2026-11-15]: lunes a viernes, menos los festivos nacionales si el tenant es de
/// Colombia; no es el horario de atención. Lo arma Bootstrapper: los festivos viven en Mensajería y Soporte nunca la referencia.</summary>
public interface ISupportBusinessCalendar
{
    // "date" choca con la palabra reservada Date de VB (CA1716).
    bool IsBusinessDay(DateOnly day);
}
