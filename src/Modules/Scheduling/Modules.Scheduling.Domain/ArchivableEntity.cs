namespace Modules.Scheduling.Domain;

/// <summary>Lo que se archiva en vez de borrarse: su historial de citas sigue apuntándole. Archivar dos veces no cambia nada.</summary>
public abstract class ArchivableEntity : VersionedEntity
{
    public DateTimeOffset? ArchivedAt { get; protected set; }

    public bool Archived => ArchivedAt is not null;

    public void Archive(DateTimeOffset now)
    {
        if (Archived)
        {
            return;
        }

        EnsureCanArchive();
        ArchivedAt = now;
        Touch(now);
    }

    public void Unarchive(DateTimeOffset now)
    {
        if (!Archived)
        {
            return;
        }

        ArchivedAt = null;
        Touch(now);
    }

    protected virtual void EnsureCanArchive()
    {
    }
}
