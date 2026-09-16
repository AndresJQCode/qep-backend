using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Storage.UnitTests;

// Dobles de los puertos de Storage.Application, a mano y sin librería de mocking, como el resto del
// repositorio: devuelven lo sembrado y anotan lo que reciben.

internal sealed class InMemoryFileResourceRepository(params FileResource[] resources) : IFileResourceRepository
{
    public void Add(FileResource resource) => throw new NotSupportedException();

    public Task<FileResource?> GetAsync(FileResourceId id, CancellationToken cancellationToken) =>
        Task.FromResult(resources.FirstOrDefault(resource => resource.Id == id));

    public Task<(IReadOnlyList<FileResource> Items, int TotalCount)> SearchAsync(
        Guid tenantId,
        string? search,
        FileResourceStatus? status,
        string? kind,
        string? category,
        string? tag,
        FileOwnerFilter? owner,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>El bucket privado: sólo firma descargas, con la clave a la vista para que la prueba vea
/// qué se firmó.</summary>
internal sealed class SigningObjectStorage : IObjectStorage
{
    public const string SignedBaseUrl = "https://r2.test";

    public Task<Uri> CreatePresignedUploadUrlAsync(
        string key, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, string? downloadFileName, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"{SignedBaseUrl}/{key}?signature=test"));

    public Task<Uri> CreatePresignedDownloadUrlAsync(
        string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task PromoteAsync(
        string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task UploadAsync(
        string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>El bucket público: sólo arma URLs.</summary>
internal sealed class FixedPublicObjectStorage : IPublicObjectStorage
{
    public const string BaseUrl = "https://assets-qep.example.co";

    public bool IsConfigured => true;

    public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public string GetUrl(string publicKey) => $"{BaseUrl}/{publicKey}";

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

    public void Publish(
        Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
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
