using Microsoft.Extensions.Logging.Abstractions;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.HrDocuments;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado», «Por cada id»): la purga del PDF que Hr soltó, con dobles. Un borrado equivocado no se
/// recupera, así que cada prueba fija una forma de no borrar: la referencia que aparece bajo el candado, el archivo de otro dueño, de otro
/// tenant o que ya no está <c>Available</c>, y la falla de R2 o de la pregunta, que no marcan el mensaje. Regla R1 del plan: cada archivo
/// cierra su propia transacción, también el que se salta, y el mensaje se marca con el último guardado.
/// </summary>
public sealed class HrDocumentPurgeTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FileResource NewFile(Guid tenantId, FileOwnerType ownerType = FileOwnerType.HrDocument, bool withVariant = false)
    {
        var id = FileResourceId.New();
        var resource = FileResource.CreatePendingUpload(
            id, tenantId, tenantId, ownerType, "contrato.pdf", "application/pdf", 1024, $"files/{id}", Now.AddDays(-3));
        resource.CompleteUpload("abc", 1024, Now.AddDays(-3));
        if (withVariant)
        {
            resource.AddVariant("thumb", $"files/{id}/thumb", "image/webp", 10, 10, 10);
        }

        resource.MarkClean(Now.AddDays(-3));
        return resource;
    }

    private sealed class World
    {
        public World() => References.Store = Store;

        public InMemoryPurgeStore Store { get; } = new();

        public QueuedReferences References { get; } = new();

        public RecordingObjectStorage Objects { get; } = new();

        public RecordingStorageAuditPublisher Audit { get; } = new();

        public int Marks { get; private set; }

        public int SavesAtMark { get; private set; } = -1;

        public HrDocumentPurge Purge => new(Store, References, Objects, Audit, new FixedClock(Now), NullLogger<HrDocumentPurge>.Instance);

        public Task MarkProcessed()
        {
            Marks++;
            SavesAtMark = Store.Saves;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task AnUnreferencedHrDocumentIsDeletedPurgedAndAudited()
    {
        var world = new World();
        var file = world.Store.Add(NewFile(TenantId, withVariant: true));

        await world.Purge.PurgeAsync(TenantId, [file.Id.Value], world.MarkProcessed, Ct);

        Assert.Equal([file.StorageKey, $"{file.StorageKey}/thumb"], world.Objects.Deleted);
        Assert.Equal(FileResourceStatus.Purged, file.Status);
        Assert.Equal(Now, file.DeletedAt);
        var audit = Assert.Single(world.Audit.SystemEntries);
        Assert.Equal((TenantId, "storage.file.purged", "file", file.Id.ToString(), "hr_document_released"), audit);
        Assert.Equal(1, world.Marks);
        // Marcado antes del único guardado, que es el último: el inbox viaja con el archivo.
        Assert.Equal(0, world.SavesAtMark);
        Assert.Equal(1, world.Store.Saves);
        Assert.Equal(0, world.Store.Abandons);
        Assert.Equal([file.Id.Value], Assert.Single(world.References.Asked));
    }

    [Fact]
    public async Task AReferenceThatAppearsUnderTheLockKeepsTheFile()
    {
        var world = new World();
        var file = world.Store.Add(NewFile(TenantId));
        world.References.Respond([file.Id.Value]);

        await world.Purge.PurgeAsync(TenantId, [file.Id.Value], world.MarkProcessed, Ct);

        Assert.Empty(world.Objects.Deleted);
        Assert.Equal(FileResourceStatus.Available, file.Status);
        Assert.Empty(world.Audit.SystemEntries);
        Assert.Equal(1, world.Marks);
        Assert.Equal(1, world.Store.Saves);
        // La pregunta se hace con el candado tomado, no antes.
        Assert.Equal(1, world.References.LocksHeldWhenAsked.Single());
    }

    [Fact]
    public async Task AnotherOwnerAnotherTenantOrNotAvailableIsSkipped()
    {
        var world = new World();
        var product = world.Store.Add(NewFile(TenantId, FileOwnerType.Product));
        var foreign = world.Store.Add(NewFile(Guid.CreateVersion7()));
        var purged = world.Store.Add(NewFile(TenantId));
        purged.PurgeReleasedHrDocument(Now.AddDays(-1));

        await world.Purge.PurgeAsync(TenantId, [product.Id.Value, foreign.Id.Value, purged.Id.Value, Guid.CreateVersion7()], world.MarkProcessed, Ct);

        Assert.Empty(world.Objects.Deleted);
        Assert.Empty(world.Audit.SystemEntries);
        Assert.Empty(world.References.Asked);
        Assert.Equal(FileResourceStatus.Available, product.Status);
        Assert.Equal(FileResourceStatus.Available, foreign.Status);
        Assert.Equal(1, world.Marks);
        // R1: cada id cierra su transacción, también los saltados; la marca va con el último.
        Assert.Equal(4, world.Store.Saves);
        Assert.Equal(3, world.SavesAtMark);
    }

    [Fact]
    public async Task AnEmptyListIsMarkedInOneSave()
    {
        var world = new World();

        await world.Purge.PurgeAsync(TenantId, [], world.MarkProcessed, Ct);

        Assert.Equal(1, world.Marks);
        Assert.Equal(0, world.SavesAtMark);
        Assert.Equal(1, world.Store.Saves);
    }

    [Fact]
    public async Task AFailingR2OrReferenceQueryMarksNothing()
    {
        var r2 = new World();
        var r2File = r2.Store.Add(NewFile(TenantId));
        r2.Objects.FailingKey = r2File.StorageKey;

        await Assert.ThrowsAsync<InvalidOperationException>(() => r2.Purge.PurgeAsync(TenantId, [r2File.Id.Value], r2.MarkProcessed, Ct));

        Assert.Equal(0, r2.Marks);
        Assert.Equal(1, r2.Store.Abandons);
        Assert.Equal(0, r2.Store.Saves);
        Assert.Equal(FileResourceStatus.Available, r2File.Status);
        Assert.Empty(r2.Audit.SystemEntries);

        var query = new World();
        var queryFile = query.Store.Add(NewFile(TenantId));
        query.References.Failing = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => query.Purge.PurgeAsync(TenantId, [queryFile.Id.Value], query.MarkProcessed, Ct));

        Assert.Equal(0, query.Marks);
        Assert.Equal(1, query.Store.Abandons);
        Assert.Equal(0, query.Store.Saves);
        Assert.Empty(query.Objects.Deleted);
        Assert.Equal(FileResourceStatus.Available, queryFile.Status);
    }

    [Fact]
    public async Task EachFileIsSavedBeforeTheNextOne()
    {
        var world = new World();
        var first = world.Store.Add(NewFile(TenantId));
        var second = world.Store.Add(NewFile(TenantId));
        world.Objects.FailingKey = second.StorageKey;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => world.Purge.PurgeAsync(TenantId, [first.Id.Value, second.Id.Value], world.MarkProcessed, Ct));

        Assert.Equal(FileResourceStatus.Purged, first.Status);
        Assert.Equal([FileResourceStatus.Purged], world.Store.SavedStatuses[first.Id]);
        Assert.False(world.Store.SavedStatuses.ContainsKey(second.Id));
        Assert.Equal(FileResourceStatus.Available, second.Status);
        Assert.Equal(1, world.Store.Saves);
        Assert.Equal(1, world.Store.Abandons);
        Assert.Equal(0, world.Marks);
    }

    /// <summary>
    /// El candado en memoria: filtra por tenant como el <c>SELECT … FOR UPDATE</c>, cuenta guardados y abandonos y anota el estado de
    /// cada archivo bloqueado en el momento de guardarlo. Un abandono no deshace lo que cambió en memoria; por eso la purga borra en R2
    /// antes de tocar el estado.
    /// </summary>
    private sealed class InMemoryPurgeStore : IHrDocumentPurgeStore
    {
        private readonly List<FileResource> _files = [];
        private readonly List<FileResource> _locked = [];

        public int Saves { get; private set; }

        public int Abandons { get; private set; }

        public int LocksHeld => _locked.Count;

        public Dictionary<FileResourceId, List<FileResourceStatus>> SavedStatuses { get; } = [];

        public FileResource Add(FileResource file)
        {
            _files.Add(file);
            return file;
        }

        public Task<FileResource?> LockAsync(Guid tenantId, FileResourceId id, CancellationToken ct)
        {
            var file = _files.SingleOrDefault(candidate => candidate.Id == id && candidate.TenantId == tenantId);
            if (file is not null)
            {
                _locked.Add(file);
            }

            return Task.FromResult(file);
        }

        public Task SaveAsync(CancellationToken ct)
        {
            Saves++;
            foreach (var file in _locked)
            {
                if (!SavedStatuses.TryGetValue(file.Id, out var statuses))
                {
                    statuses = [];
                    SavedStatuses[file.Id] = statuses;
                }

                statuses.Add(file.Status);
            }

            _locked.Clear();
            return Task.CompletedTask;
        }

        public Task AbandonAsync()
        {
            Abandons++;
            _locked.Clear();
            return Task.CompletedTask;
        }
    }

    /// <summary>Hr de mentira: responde lo que la prueba encola (si no, nada en uso) y puede estar caído.</summary>
    private sealed class QueuedReferences : IHrDocumentReferences
    {
        private readonly Queue<IReadOnlySet<Guid>> _responses = new();

        public InMemoryPurgeStore? Store { get; set; }

        public bool Failing { get; set; }

        public List<List<Guid>> Asked { get; } = [];

        public List<int> LocksHeldWhenAsked { get; } = [];

        public void Respond(params Guid[] referenced) => _responses.Enqueue(referenced.ToHashSet());

        public Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
        {
            if (Failing)
            {
                throw new InvalidOperationException("Hr is down.");
            }

            Asked.Add([.. fileIds]);
            LocksHeldWhenAsked.Add(Store!.LocksHeld);
            return Task.FromResult(_responses.TryDequeue(out var response) ? response : new HashSet<Guid>());
        }
    }

    /// <summary>El bucket privado: anota cada clave borrada y puede fallar en una.</summary>
    private sealed class RecordingObjectStorage : IObjectStorage
    {
        public string? FailingKey { get; set; }

        public List<string> Deleted { get; } = [];

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            if (string.Equals(key, FailingKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("R2 is down.");
            }

            Deleted.Add(key);
            return Task.CompletedTask;
        }

        public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
