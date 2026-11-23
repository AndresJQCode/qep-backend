namespace Modules.Scheduling.Domain;

/// <summary>Spec 2026-11-21, «Citas». <c>Cancelled</c>, <c>Completed</c> y <c>NoShow</c> son terminales; una cita <c>Pending</c> ya aparta su cupo.</summary>
public enum AppointmentStatus
{
    Pending,
    Confirmed,
    Cancelled,
    Completed,
    NoShow,
}
