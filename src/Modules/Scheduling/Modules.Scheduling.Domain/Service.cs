namespace Modules.Scheduling.Domain;

/// <summary>
/// Qué se agenda y cuánto dura. Propio de la Agenda (spec 2026-11-21, D-3): el vínculo con un producto del Catálogo queda para después.
/// El margen (<see cref="BufferMinutes"/>) ocupa al recurso pero no se le muestra al cliente. Sin cupo ni modo: eso es de la franja.
/// </summary>
public sealed class Service : ArchivableEntity
{
    public const int MaxDurationMinutes = 480;

    public const int MaxBufferMinutes = 240;

    private Service()
    {
    }

    public ServiceId Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int DurationMinutes { get; private set; }

    public int BufferMinutes { get; private set; }

    public bool BookableByChannel { get; private set; }

    /// <summary>Lo que el servicio ocupa del recurso: la duración más el margen.</summary>
    public int OccupiedMinutes => DurationMinutes + BufferMinutes;

    public static Service Create(
        ServiceId id, Guid tenantId, string name, int durationMinutes, int bufferMinutes, bool bookableByChannel, DateTimeOffset now)
    {
        var service = new Service { Id = id };
        service.Apply(name, durationMinutes, bufferMinutes, bookableByChannel);
        service.Initialize(tenantId, now);
        return service;
    }

    public void Update(string name, int durationMinutes, int bufferMinutes, bool bookableByChannel, DateTimeOffset now)
    {
        Apply(name, durationMinutes, bufferMinutes, bookableByChannel);
        Touch(now);
    }

    private void Apply(string name, int durationMinutes, int bufferMinutes, bool bookableByChannel)
    {
        var validName = RequireName(name);
        RequireMultipleOfFive(durationMinutes, 5, MaxDurationMinutes, nameof(durationMinutes));
        RequireMultipleOfFive(bufferMinutes, 0, MaxBufferMinutes, nameof(bufferMinutes));

        Name = validName;
        DurationMinutes = durationMinutes;
        BufferMinutes = bufferMinutes;
        BookableByChannel = bookableByChannel;
    }

    private static void RequireMultipleOfFive(int value, int min, int max, string parameter)
    {
        if (value < min || value > max || value % 5 != 0)
        {
            throw new ArgumentOutOfRangeException(parameter, value, $"Must be a multiple of 5 between {min} and {max}.");
        }
    }
}
