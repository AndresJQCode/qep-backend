namespace Modules.Scheduling.Domain;

/// <summary>
/// La sede: la agenda de un consultorio, que agrupa recursos. Cada tenant tiene exactamente una principal (<see cref="IsMain"/>), la
/// aprovisiona el primer acceso y no se archiva; las demás sólo las crea quien configura la estructura.
/// </summary>
public sealed class Location : ArchivableEntity
{
    private Location()
    {
    }

    public LocationId Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public bool IsMain { get; private set; }

    public static Location Create(LocationId id, Guid tenantId, string name, bool isMain, DateTimeOffset now)
    {
        var location = new Location { Id = id, Name = RequireName(name), IsMain = isMain };
        location.Initialize(tenantId, now);
        return location;
    }

    public void Rename(string name, DateTimeOffset now)
    {
        Name = RequireName(name);
        Touch(now);
    }

    protected override void EnsureCanArchive()
    {
        if (IsMain)
        {
            throw new SchedulingDomainException(
                SchedulingErrorCodes.MainLocationCannotBeArchived, "The main location cannot be archived.");
        }
    }
}
