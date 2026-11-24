namespace Modules.Scheduling.Domain;

public readonly record struct AppointmentId(Guid Value)
{
    public static AppointmentId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
