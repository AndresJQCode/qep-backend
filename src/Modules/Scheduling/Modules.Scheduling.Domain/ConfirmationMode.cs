namespace Modules.Scheduling.Domain;

/// <summary>Cómo nacen las citas que reserva un canal (spec 2026-11-21, D-5). En los dos casos la cita aparta el cupo desde el primer momento.</summary>
public enum ConfirmationMode
{
    Automatic,
    Manual,
}
