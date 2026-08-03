namespace Modules.Storage.Domain;

// Upload lifecycle of a file resource (capability contract). Only Available is
// downloadable; a soft-deleted resource is retained before physical purge.
public enum FileResourceStatus
{
    PendingUpload = 1,
    PendingScan = 2,
    Available = 3,
    Quarantined = 4,
    Deleted = 5,
    Purged = 6
}

public enum FileOwnerType
{
    User = 1,
    Entity = 2,
    System = 3
}
