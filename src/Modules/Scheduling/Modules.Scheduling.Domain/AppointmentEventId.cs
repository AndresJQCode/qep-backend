namespace Modules.Scheduling.Domain;

public readonly record struct AppointmentEventId(Guid Value)
{
    public static AppointmentEventId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
