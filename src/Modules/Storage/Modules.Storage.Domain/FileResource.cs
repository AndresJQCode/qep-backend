namespace Modules.Storage.Domain;

/// <summary>
/// El registro lógico en QEP de un archivo o recurso binario; el objeto físico vive en
/// almacenamiento de objetos compatible con S3 (ADR 0020) en <see cref="StorageKey"/>. La
/// metadata está acotada al tenant. El ciclo de subida es PendingUpload → PendingScan →
/// Available (o Quarantined); sólo los Available se descargan; el borrado lógico precede al purgado.
/// </summary>
public sealed class FileResource
{
    private readonly List<FileVariant> _variants = [];

    private FileResource()
    {
    }

    private FileResource(
        FileResourceId id,
        Guid tenantId,
        Guid ownerId,
        FileOwnerType ownerType,
        string name,
        string mimeType,
        long declaredSizeBytes,
        string storageKey,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        OwnerId = ownerId;
        OwnerType = ownerType;
        Name = name;
        MimeType = mimeType;
        SizeBytes = declaredSizeBytes;
        StorageKey = storageKey;
        Status = FileResourceStatus.PendingUpload;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public FileResourceId Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid OwnerId { get; private set; }

    public FileOwnerType OwnerType { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string MimeType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public string StorageKey { get; private set; } = string.Empty;

    public string? Checksum { get; private set; }

    public string? Category { get; private set; }

    public string[] Tags { get; private set; } = [];

    public string? PublicStorageKey { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public bool IsPublic => PublicStorageKey is not null;

    public IReadOnlyCollection<FileVariant> Variants => _variants.AsReadOnly();

    public FileResourceStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public static FileResource CreatePendingUpload(
        FileResourceId id,
        Guid tenantId,
        Guid ownerId,
        FileOwnerType ownerType,
        string name,
        string mimeType,
        long declaredSizeBytes,
        string storageKey,
        DateTimeOffset createdAt) =>
        new(
            id,
            tenantId,
            ownerId,
            ownerType,
            name,
            mimeType,
            declaredSizeBytes,
            storageKey,
            createdAt);

    // Se llama después de que el cliente hace PUT del objeto. Verifica el tamaño almacenado y
    // registra el checksum, y después pasa a PendingScan. Las entradas idempotentes se rechazan:
    // completar sólo aplica a un recurso PendingUpload.
    public void CompleteUpload(string checksum, long verifiedSizeBytes, DateTimeOffset occurredAt)
    {
        if (Status is not FileResourceStatus.PendingUpload)
        {
            throw new StorageDomainException(
                "storage.file.invalid_state",
                "Upload can only be completed for a resource that is pending upload.");
        }

        if (verifiedSizeBytes <= 0)
        {
            throw new StorageDomainException(
                "storage.file.empty",
                "The uploaded object is empty or missing.");
        }

        Checksum = checksum;
        SizeBytes = verifiedSizeBytes;
        Status = FileResourceStatus.PendingScan;
        UpdatedAt = occurredAt;
    }

    public void MarkClean(DateTimeOffset occurredAt)
    {
        RequireStatus(FileResourceStatus.PendingScan);
        Status = FileResourceStatus.Available;
        UpdatedAt = occurredAt;
    }

    public void Promote(string finalStorageKey, DateTimeOffset occurredAt)
    {
        RequireStatus(FileResourceStatus.PendingScan);
        if (string.IsNullOrWhiteSpace(finalStorageKey))
        {
            throw new StorageDomainException(
                "storage.file.final_key_required",
                "A final storage key is required.");
        }

        StorageKey = finalStorageKey;
        Status = FileResourceStatus.Available;
        UpdatedAt = occurredAt;
    }

    public void Quarantine(DateTimeOffset occurredAt)
    {
        RequireStatus(FileResourceStatus.PendingScan);
        Status = FileResourceStatus.Quarantined;
        UpdatedAt = occurredAt;
    }

    public void SoftDelete(DateTimeOffset occurredAt)
    {
        if (Status is FileResourceStatus.Deleted or FileResourceStatus.Purged)
        {
            throw new StorageDomainException(
                "storage.file.invalid_state",
                "The resource is already deleted.");
        }

        Status = FileResourceStatus.Deleted;
        DeletedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public void PurgeAbandonedUpload(DateTimeOffset occurredAt)
    {
        RequireStatus(FileResourceStatus.PendingUpload);
        Status = FileResourceStatus.Purged;
        DeletedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public void UpdateMetadata(
        string? category,
        IEnumerable<string>? tags,
        DateTimeOffset occurredAt)
    {
        if (Status is FileResourceStatus.Deleted or FileResourceStatus.Purged)
        {
            throw new StorageDomainException(
                "storage.file.invalid_state",
                "Metadata cannot be changed on a deleted resource.");
        }

        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        if (normalizedCategory?.Length > 80)
        {
            throw new StorageDomainException(
                "storage.file.category_too_long",
                "The category cannot exceed 80 characters.");
        }

        var normalizedTags = (tags ?? [])
            .Select(tag => tag?.Trim().ToLowerInvariant())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalizedTags.Length > 10 || normalizedTags.Any(tag => tag.Length > 40))
        {
            throw new StorageDomainException(
                "storage.file.tags_invalid",
                "A file can have up to 10 tags of 40 characters each.");
        }

        Category = normalizedCategory;
        Tags = normalizedTags;
        UpdatedAt = occurredAt;
    }

    public void AddVariant(
        string name,
        string storageKey,
        string mimeType,
        int width,
        int height,
        long sizeBytes)
    {
        RequireStatus(FileResourceStatus.PendingScan);
        if (_variants.Any(variant => string.Equals(variant.Name, name, StringComparison.Ordinal)))
        {
            throw new StorageDomainException(
                "storage.file.variant_exists",
                $"The variant '{name}' already exists.");
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(storageKey) ||
            string.IsNullOrWhiteSpace(mimeType) || width <= 0 || height <= 0 || sizeBytes <= 0)
        {
            throw new StorageDomainException(
                "storage.file.variant_invalid",
                "The generated file variant is invalid.");
        }

        _variants.Add(new FileVariant(Id, name, storageKey, mimeType, width, height, sizeBytes));
    }

    public FileVariant GetVariant(string name) =>
        _variants.FirstOrDefault(variant =>
            string.Equals(variant.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new StorageDomainException(
            "storage.file.variant_not_found",
            $"The variant '{name}' does not exist for this file.");

    public void Publish(string publicStorageKey, DateTimeOffset occurredAt)
    {
        EnsureDownloadable();
        if (!MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageDomainException(
                "storage.file.public_image_required",
                "Only images can be published.");
        }

        if (string.IsNullOrWhiteSpace(publicStorageKey))
        {
            throw new StorageDomainException(
                "storage.file.public_key_required",
                "A public storage key is required.");
        }

        PublicStorageKey = publicStorageKey;
        PublishedAt = occurredAt;
        UpdatedAt = occurredAt;
    }

    public void Unpublish(DateTimeOffset occurredAt)
    {
        PublicStorageKey = null;
        PublishedAt = null;
        UpdatedAt = occurredAt;
    }

    // Sólo a un recurso Available se le emite una URL de descarga (invariante de la capacidad).
    public void EnsureDownloadable()
    {
        if (Status is not FileResourceStatus.Available)
        {
            throw new StorageDomainException(
                "storage.file.not_available",
                "Only an available resource can be downloaded.");
        }
    }

    private void RequireStatus(FileResourceStatus expected)
    {
        if (Status != expected)
        {
            throw new StorageDomainException(
                "storage.file.invalid_state",
                $"Expected the resource to be {expected} but it was {Status}.");
        }
    }
}
