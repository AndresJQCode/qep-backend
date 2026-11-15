namespace Modules.Support.Domain;

/// <summary>Spec 2026-11-14 §7.2: el calendario de la bandeja del tenant (su horario predeterminado, festivos y excepciones de
/// fecha). Lo arma Bootstrapper: Soporte nunca referencia Mensajería.</summary>
public interface ISupportBusinessCalendar
{
    // "date" choca con la palabra reservada Date de VB (CA1716).
    bool IsBusinessDay(DateOnly day);
}
