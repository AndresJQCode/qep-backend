using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

// Dobles de los puertos de Storage que usan los adaptadores del composition root. A mano y sin
// librería de mocking, como el resto del repositorio: anotan lo que reciben.

/// <summary>Los archivos que siembra la prueba, por id. Sólo las lecturas por id: es lo único que leen
/// el publicador de comprobantes y los lookups. Cuenta cada llamada, para que una prueba afirme que una
/// lectura en lote no se volvió una por id.</summary>
internal sealed class InMemoryFileResourceRepository(params FileResource[] resources)
    : IFileResourceRepository
{
    public int GetCalls { get; private set; }

    public int ListByIdsCalls { get; private set; }

    public void Add(FileResource resource) => throw new NotSupportedException();

    public Task<FileResource?> GetAsync(FileResourceId id, CancellationToken cancellationToken)
    {
        GetCalls++;
        return Task.FromResult(resources.FirstOrDefault(resource => resource.Id == id));
    }

    public Task<IReadOnlyList<FileResource>> ListByIdsAsync(
        IReadOnlyCollection<FileResourceId> ids, CancellationToken cancellationToken)
    {
        ListByIdsCalls++;
        return Task.FromResult<IReadOnlyList<FileResource>>(
            resources.Where(resource => ids.Contains(resource.Id)).ToList());
    }

    public Task<(IReadOnlyList<FileResource> Items, int TotalCount)> SearchAsync(
        Guid tenantId,
        string? search,
        FileResourceStatus? status,
        string? kind,
        string? category,
        string? tag,
        FileOwnerFilter? owner,
        IReadOnlyCollection<FileOwnerType> excludedOwnerTypes,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>El bucket público: anota cada copia (clave pública → clave privada de origen) y cada
/// borrado. <see cref="GetUrl"/> arma la URL con <see cref="BaseUrl"/>, como R2PublicObjectStorage con
/// Storage:R2:PublicBaseUrl.</summary>
internal sealed class RecordingPublicObjectStorage : IPublicObjectStorage
{
    public const string BaseUrl = "https://assets-qep.example.co";

    public Dictionary<string, string> Copies { get; } = new(StringComparer.Ordinal);

    public List<string> DeletedKeys { get; } = [];

    /// <summary>Si no es null, <see cref="DeleteAsync"/> la lanza sin anotar el borrado.</summary>
    public Exception? DeleteFailure { get; set; }

    /// <summary>Lo que se subió directo al bucket público: clave, tipo y bytes.</summary>
    public List<(string Key, string ContentType, byte[] Content)> Uploads { get; } = [];

    public bool IsConfiguredValue { get; set; } = true;

    public bool IsConfigured => IsConfiguredValue;

    public Task CopyFromPrivateAsync(
        string privateKey, string publicKey, CancellationToken cancellationToken)
    {
        Copies[publicKey] = privateKey;
        return Task.CompletedTask;
    }

    public Task UploadAsync(
        string publicKey, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        Uploads.Add((publicKey, contentType, content.ToArray()));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
    {
        if (DeleteFailure is not null)
        {
            return Task.FromException(DeleteFailure);
        }

        DeletedKeys.Add(publicKey);
        return Task.CompletedTask;
    }

    // El publicador tampoco pregunta si una copia existe: eso es del movimiento de Storage.
    public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

    // El publicador de comprobantes no lista el bucket: eso es de la reconciliación de Storage.
    public Task<PublicObjectPage> ListAsync(
        string prefix, string? continuationToken, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class CountingStorageUnitOfWork : IStorageUnitOfWork
{
    public int Saves { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        Saves++;
        return Task.FromResult(1);
    }
}

internal sealed class RecordingStorageAuditPublisher : IStorageAuditPublisher
{
    public List<string> Actions { get; } = [];

    /// <summary>Quién hizo cada acción de miembro: <c>(action, actorId)</c>.</summary>
    public List<(string Action, Guid ActorId)> ByActor { get; } = [];

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt)
    {
        Actions.Add(action);
        ByActor.Add((action, actorId));
    }

    public void PublishSystem(
        Guid? tenantId, string action, string resourceType, string resourceId, string outcome, DateTimeOffset occurredAt) =>
        Actions.Add(action);
}

internal sealed class AllowAllExecutionContext(Guid tenantId) : IExecutionContext
{
    public Guid SubjectId { get; } = Guid.CreateVersion7();

    public TenantId TenantId { get; } = new(tenantId);

    public bool HasPermission(string permission) => true;
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; } = now;
}

/// <summary>Las subidas sin miembro de Storage, en memoria: cada archivo recuerda su dueño y cómo termina al completar. Un par
/// (tenant, tipo de dueño, dueño) que no casa es <see cref="SystemUploadOutcome.NotFound"/>, como en <c>StorageSystemUploads</c>.</summary>
internal sealed class FakeStorageSystemUploads : IStorageSystemUploads
{
    public sealed record Opened(
        Guid TenantId, FileOwnerType OwnerType, Guid OwnerId, string Name, string MimeType, long SizeBytes, long MaxSizeBytes);

    private sealed record Seeded(Guid TenantId, FileOwnerType OwnerType, Guid OwnerId, SystemUploadOutcome Outcome, SystemFileInfo Info);

    private readonly Dictionary<Guid, Seeded> _files = [];

    public List<Opened> Opens { get; } = [];

    public SystemUploadSession Session { get; } = new(
        Guid.CreateVersion7(),
        new Uri("https://r2.example.co/staging/banner?signature=abc"),
        new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = "image/png", ["If-None-Match"] = "*" });

    /// <summary>Un archivo que, al completarse, termina en <paramref name="outcome"/>.</summary>
    public Guid Seed(
        Guid tenantId, FileOwnerType ownerType, Guid ownerId, SystemUploadOutcome outcome, string mimeType = "image/png", long sizeBytes = 2048,
        DateTimeOffset createdAt = default, Guid? fileId = null, string name = "banner.png")
    {
        var id = fileId ?? Guid.CreateVersion7();
        _files[id] = new Seeded(
            tenantId, ownerType, ownerId, outcome, new SystemFileInfo(id, name, mimeType, sizeBytes, createdAt));
        return id;
    }

    public Task<SystemUploadSession> OpenAsync(
        Guid tenantId, FileOwnerType ownerType, Guid ownerId, string name, string mimeType, long sizeBytes, long maxSizeBytes,
        CancellationToken cancellationToken)
    {
        // La misma declaración que valida StorageSystemUploads.OpenAsync: un nombre que no casa con su tipo no abre nada.
        FileUploadPolicy.ValidateDeclaration(name, mimeType, sizeBytes, maxSizeBytes, ownerType);
        Opens.Add(new Opened(tenantId, ownerType, ownerId, name, mimeType, sizeBytes, maxSizeBytes));
        return Task.FromResult(Session);
    }

    public Task<SystemUploadResult> CompleteAsync(
        Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(new SystemUploadResult(Find(tenantId, fileId, ownerType, ownerId)?.Outcome ?? SystemUploadOutcome.NotFound));

    public Task<SystemFileInfo?> FindAvailableAsync(
        Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(tenantId, fileId, ownerType, ownerId) is { Outcome: SystemUploadOutcome.Available } file ? file.Info : null);

    /// <summary>Los ids para los que se pidió una URL de descarga.</summary>
    public List<Guid> Downloads { get; } = [];

    public Task<Uri> CreateDownloadUrlAsync(Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        Downloads.Add(fileId);
        return Task.FromResult(new Uri($"https://r2.example.co/download/{fileId}?signature=abc"));
    }

    public Task<SystemStoredFile> StoreAsync(
        Guid tenantId, FileOwnerType ownerType, Guid ownerId, string name, string mimeType, byte[] content, long maxSizeBytes,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    private Seeded? Find(Guid tenantId, Guid fileId, FileOwnerType ownerType, Guid ownerId) =>
        _files.TryGetValue(fileId, out var file) && file.TenantId == tenantId && file.OwnerType == ownerType && file.OwnerId == ownerId
            ? file
            : null;
}

/// <summary>El bucket privado, sólo para leer bytes: <see cref="DownloadAsync"/> devuelve lo sembrado por clave.</summary>
internal sealed class BytesObjectStorage : IObjectStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
        Objects.TryGetValue(key, out var bytes) ? Task.FromResult(bytes) : throw new InvalidOperationException($"No object at '{key}'.");

    public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
}
