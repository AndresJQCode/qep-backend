using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.UnreferencedFiles;

namespace Modules.Storage.UnitTests;

/// <summary>Las filas de Storage en memoria para los barridos de archivos sin uso. Lista sin filtrar por dueño (eso lo hace el SQL, y así
/// se prueba que el barrido no confía en él) y reclama sólo lo vivo y del dueño pedido, como el SQL.</summary>
internal sealed class FakeUnreferencedFiles : IUnreferencedFileCandidates
{
    private readonly List<FileResource> _resources = [];
    private FileResource? _claimed;

    public DateTimeOffset? LastCreatedBefore { get; private set; }

    public HashSet<FileResourceId> HeldElsewhere { get; } = [];

    public List<FileResourceId> Claimed { get; } = [];

    public List<FileResourceId> Committed { get; } = [];

    public List<FileResourceId> Abandoned { get; } = [];

    public FileResource Seed(Guid tenantId, FileOwnerType ownerType, DateTimeOffset createdAt)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), tenantId, tenantId, ownerType,
            "banner.png", "image/png", 2048, $"staging/tenants/{tenantId:N}/{Guid.NewGuid():N}", createdAt);
        file.CompleteUpload("checksum", 2048, createdAt);
        file.MarkClean(createdAt);
        _resources.Add(file);
        return file;
    }

    public Task<IReadOnlyList<UnreferencedFileCandidate>> ListStaleAsync(
        FileOwnerType ownerType, DateTimeOffset createdBefore, UnreferencedFileCandidate? after, int take, CancellationToken cancellationToken)
    {
        LastCreatedBefore = createdBefore;
        IReadOnlyList<UnreferencedFileCandidate> page = _resources
            .Where(file => file.Status is not (FileResourceStatus.Deleted or FileResourceStatus.Purged))
            .Where(file => file.CreatedAt < createdBefore)
            .Select(file => new UnreferencedFileCandidate(file.TenantId, file.Id, file.OwnerType, file.CreatedAt))
            .OrderBy(candidate => candidate.CreatedAt).ThenBy(candidate => candidate.Id.Value)
            .Where(candidate => after is null
                || candidate.CreatedAt > after.CreatedAt
                || (candidate.CreatedAt == after.CreatedAt && candidate.Id.Value.CompareTo(after.Id.Value) > 0))
            .Take(take)
            .ToList();
        return Task.FromResult(page);
    }

    public Task<FileResource?> ClaimAsync(FileOwnerType ownerType, Guid tenantId, FileResourceId id, CancellationToken cancellationToken)
    {
        Claimed.Add(id);
        _claimed = _resources.SingleOrDefault(file => file.Id == id && file.TenantId == tenantId
            && file.OwnerType == ownerType
            && file.Status is not (FileResourceStatus.Deleted or FileResourceStatus.Purged)
            && !HeldElsewhere.Contains(id));
        return Task.FromResult(_claimed);
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        Committed.Add(_claimed!.Id);
        _claimed = null;
        return Task.CompletedTask;
    }

    public Task AbandonAsync()
    {
        if (_claimed is not null)
        {
            Abandoned.Add(_claimed.Id);
        }

        _claimed = null;
        return Task.CompletedTask;
    }
}

/// <summary>El módulo que usa los archivos, de mentira: responde por tenant con los ids que se le marcaron y anota cada pregunta. Sirve de
/// <see cref="ITemplateImageReferences"/> (Messaging) y de <see cref="IChatbotMediaReferences"/> (Automations y la bandeja).</summary>
internal sealed class FakeFileReferences : ITemplateImageReferences, IChatbotMediaReferences
{
    private readonly Dictionary<Guid, HashSet<Guid>> _byTenant = [];
    private readonly List<(Guid TenantId, Guid FileId)> _late = [];

    public List<(Guid TenantId, IReadOnlyCollection<Guid> FileIds)> Calls { get; } = [];

    public Guid? FailingTenant { get; set; }

    /// <summary>Sin referencia en la primera pregunta que lo incluye; referenciado desde la siguiente (una creación que se guardó en el
    /// medio).</summary>
    public void ReferenceAfterFirstAsk(Guid tenantId, Guid fileId) => _late.Add((tenantId, fileId));

    public void Reference(Guid tenantId, Guid fileId)
    {
        if (!_byTenant.TryGetValue(tenantId, out var ids))
        {
            _byTenant[tenantId] = ids = [];
        }

        ids.Add(fileId);
    }

    public Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        Calls.Add((tenantId, fileIds.ToList()));
        if (tenantId == FailingTenant)
        {
            throw new InvalidOperationException("Simulated reference query failure.");
        }

        var referenced = _byTenant.TryGetValue(tenantId, out var ids) ? ids : [];
        IReadOnlySet<Guid> result = fileIds.Where(referenced.Contains).ToHashSet();
        foreach (var late in _late.Where(late => late.TenantId == tenantId && fileIds.Contains(late.FileId)).ToList())
        {
            _late.Remove(late);
            Reference(late.TenantId, late.FileId);
        }

        return Task.FromResult(result);
    }
}

internal sealed class FailingPublicObjectStorage : IPublicObjectStorage
{
    public Task UploadAsync(string publicKey, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) => Task.CompletedTask;

    public List<string> DeletedKeys { get; } = [];

    public string? FailingFragment { get; set; }

    public bool IsConfigured => true;

    public Task CopyFromPrivateAsync(string privateKey, string publicKey, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task DeleteAsync(string publicKey, CancellationToken cancellationToken)
    {
        if (FailingFragment is not null && publicKey.Contains(FailingFragment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated R2 failure.");
        }

        DeletedKeys.Add(publicKey);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string publicKey, CancellationToken cancellationToken) => throw new NotSupportedException();

    public string GetUrl(string publicKey) => throw new NotSupportedException();

    public Task<PublicObjectPage> ListAsync(string prefix, string? continuationToken, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class SweepAuditPublisher : IStorageAuditPublisher
{
    public List<(Guid? TenantId, string Action, string ResourceType, string ResourceId, string Outcome, DateTimeOffset OccurredAt)> Entries { get; } = [];

    public void Publish(Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
        throw new NotSupportedException("The sweep audits as the system.");

    public void PublishSystem(
        Guid? tenantId, string action, string resourceType, string resourceId, string outcome, DateTimeOffset occurredAt) =>
        Entries.Add((tenantId, action, resourceType, resourceId, outcome, occurredAt));
}
