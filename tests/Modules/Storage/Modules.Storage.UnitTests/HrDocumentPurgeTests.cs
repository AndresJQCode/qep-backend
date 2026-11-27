using Microsoft.Extensions.Logging;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.HrDocuments;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado», «Por cada id», «Reintentos»): la purga del PDF que Hr soltó, con dobles. Un borrado
/// equivocado no se recupera, así que cada prueba fija una forma de no borrar o de no dejar una fila <c>Available</c> sin objeto: la
/// referencia de la primera pregunta (sin marca), la marca <c>Deleted</c> confirmada antes de la segunda pregunta y de R2, la devolución
/// sólo con el objeto todavía en R2, el archivo de otro dueño, de otro tenant o en otro estado, y las fallas, que no marcan el mensaje.
/// Regla R1 del plan: cada archivo cierra sus transacciones (dos si se marca) y el mensaje se marca con el último guardado.
/// </summary>
public sealed class HrDocumentPurgeTests
{
    private static readonly Guid TenantId = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 15, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FileResource NewFile(
        Guid tenantId, FileOwnerType ownerType = FileOwnerType.HrDocument, bool withVariant = false, bool available = true)
    {
        var id = FileResourceId.New();
        var resource = FileResource.CreatePendingUpload(
            id, tenantId, tenantId, ownerType, "contrato.pdf", "application/pdf", 1024, $"files/{id}", Now.AddDays(-3));
        resource.CompleteUpload("abc", 1024, Now.AddDays(-3));
        if (withVariant)
        {
            resource.AddVariant("thumb", $"files/{id}/thumb", "image/webp", 10, 10, 10);
        }

        if (available)
        {
            resource.MarkClean(Now.AddDays(-3));
        }

        return resource;
    }

    /// <summary>Un PDF que un intento anterior (u otra réplica) ya marcó y confirmó.</summary>
    private static FileResource MarkedFile(Guid tenantId)
    {
        var resource = NewFile(tenantId);
        resource.BeginHrDocumentPurge(Now.AddMinutes(-1));
        return resource;
    }

    private sealed class World
    {
        public World()
        {
            References.Store = Store;
            Objects.Store = Store;
        }

        public InMemoryPurgeStore Store { get; } = new();

        public QueuedReferences References { get; } = new();

        public RecordingObjectStorage Objects { get; } = new();

        public RecordingStorageAuditPublisher Audit { get; } = new();

        public RecordingLogger Logger { get; } = new();

        public int Marks { get; private set; }

        public int SavesAtMark { get; private set; } = -1;

        public HrDocumentPurge Purge => new(Store, References, Objects, Audit, new FixedClock(Now), Logger);

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
        // Dos transacciones (la marca y la decisión); el inbox viaja con la última.
        Assert.Equal(1, world.SavesAtMark);
        Assert.Equal(2, world.Store.Saves);
        Assert.Equal(0, world.Store.Abandons);
        Assert.Equal(2, world.References.Asked.Count);
        Assert.All(world.References.Asked, asked => Assert.Equal([file.Id.Value], asked));
    }

    [Fact]
    public async Task TheMarkIsCommittedBeforeTheSecondQuestionAndR2()
    {
        var world = new World();
        var file = world.Store.Add(NewFile(TenantId));

        await world.Purge.PurgeAsync(TenantId, [file.Id.Value], world.MarkProcessed, Ct);

        Assert.Equal([FileResourceStatus.Deleted, FileResourceStatus.Purged], world.Store.SavedStatuses[file.Id]);
        Assert.Equal(2, world.References.Asked.Count);
        Assert.Equal([0, 1], world.References.SavesWhenAsked);
        Assert.Equal([1], world.Objects.SavesWhenDeleted);
        Assert.Equal(1, world.SavesAtMark);
        Assert.Equal(2, world.Store.Saves);
    }

    [Fact]
    public async Task AReferenceInTheFirstQuestionKeepsTheFileUnmarked()
    {
        var world = new World();
        var file = world.Store.Add(NewFile(TenantId));
        world.References.Respond(file.Id.Value);

        await world.Purge.PurgeAsync(TenantId, [file.Id.Value], world.MarkProcessed, Ct);

        Assert.Equal([FileResourceStatus.Available], world.Store.SavedStatuses[file.Id]);
        Assert.Empty(world.Objects.Deleted);
        Assert.Empty(world.Audit.SystemEntries);
        Assert.Single(world.References.Asked);
        Assert.Equal(1, world.Marks);
        Assert.Equal(1, world.Store.Saves);
        // La pregunta se hace con el candado tomado, no antes.
        Assert.Equal([1], world.References.LocksHeldWhenAsked);
    }

    [Fact]
    public async Task AReferenceInTheSecondQuestionRestoresTheFile()
    {
        var world = new World();
        var file = world.Store.Add(NewFile(TenantId));
        world.References.Respond();
        world.References.Respond(file.Id.Value);

        await world.Purge.PurgeAsync(TenantId, [file.Id.Value], world.MarkProcessed, Ct);

        Assert.Equal([FileResourceStatus.Deleted, FileResourceStatus.Available], world.Store.SavedStatuses[file.Id]);
        Assert.Null(file.DeletedAt);
        Assert.Empty(world.Objects.Deleted);
        Assert.Empty(world.Audit.SystemEntries);
        Assert.Single(world.Logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(1, world.Marks);
    }

    [Fact]
    public async Task AFailedSaveAfterR2LeavesTheFileDeleted()
    {
        var world = new World();
        var file = world.Store.Add(NewFile(TenantId));
        world.Store.FailSaveWhen = candidate => candidate.Status == FileResourceStatus.Purged;

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Purge.PurgeAsync(TenantId, [file.Id.Value], world.MarkProcessed, Ct));

        Assert.Equal([FileResourceStatus.Deleted], world.Store.SavedStatuses[file.Id]);
        Assert.Contains(file.StorageKey, world.Objects.Deleted);
        Assert.Equal(1, world.Store.Abandons);
    }

    [Theory]
    [InlineData(false, false)] // tras la marca, antes de la segunda pregunta o con falla de R2: objeto presente, sin uso → borra y Purged
    [InlineData(true, false)] // tras borrar en R2 con guardado fallido: objeto ausente, sin uso → DeleteAsync igual (idempotente) y Purged
    [InlineData(false, true)] // objeto presente, con uso → RestoreHrDocument
    [InlineData(true, true)] // objeto ausente, con uso → Purged, auditoría y un Error en el logger
    public async Task AMarkedFileIsResumedAtTheSecondQuestion(bool objectMissing, bool referenced)
    {
        var world = new World();
        var file = world.Store.Add(MarkedFile(TenantId));
        if (objectMissing)
        {
            world.Objects.Missing.Add(file.StorageKey);
        }

        if (referenced)
        {
            world.References.Respond(file.Id.Value);
        }

        await world.Purge.PurgeAsync(TenantId, [file.Id.Value], world.MarkProcessed, Ct);

        // Sin primera pregunta ni nueva marca: retoma en la segunda, en una sola transacción.
        Assert.Single(world.References.Asked);
        Assert.Equal(1, world.Store.Saves);
        Assert.Equal(1, world.Marks);
        Assert.Equal(0, world.SavesAtMark);
        var restored = referenced && !objectMissing;
        Assert.Equal([restored ? FileResourceStatus.Available : FileResourceStatus.Purged], world.Store.SavedStatuses[file.Id]);
        if (restored)
        {
            Assert.Empty(world.Objects.Deleted);
            Assert.Empty(world.Audit.SystemEntries);
            Assert.Single(world.Logger.Entries, entry => entry.Level == LogLevel.Warning);
            return;
        }

        Assert.Equal((TenantId, "storage.file.purged", "file", file.Id.ToString(), "hr_document_released"), Assert.Single(world.Audit.SystemEntries));
        if (referenced)
        {
            // El objeto ya no estaba: no hay nada que borrar, y el riesgo residual queda a la vista.
            Assert.Empty(world.Objects.Deleted);
            Assert.Single(world.Logger.Entries, entry => entry.Level == LogLevel.Error);
        }
        else
        {
            Assert.Equal([file.StorageKey], world.Objects.Deleted);
            Assert.DoesNotContain(world.Logger.Entries, entry => entry.Level == LogLevel.Error);
        }
    }

    [Fact]
    public async Task AMarkFinishedByAnotherReplicaIsSkipped()
    {
        var world = new World();
        var file = world.Store.Add(NewFile(TenantId));
        world.Store.BeforeLock = number =>
        {
            if (number == 2)
            {
                file.PurgeReleasedHrDocument(Now);
            }
        };

        await world.Purge.PurgeAsync(TenantId, [file.Id.Value], world.MarkProcessed, Ct);

        Assert.Single(world.References.Asked);
        Assert.Empty(world.Objects.Deleted);
        Assert.Empty(world.Audit.SystemEntries);
        Assert.Equal(2, world.Store.Saves);
        Assert.Equal(1, world.Marks);
    }

    [Fact]
    public async Task AnotherOwnerAnotherTenantOrNotAvailableIsSkipped()
    {
        var world = new World();
        var product = world.Store.Add(NewFile(TenantId, FileOwnerType.Product));
        var foreign = world.Store.Add(NewFile(Guid.CreateVersion7()));
        var purged = world.Store.Add(NewFile(TenantId));
        purged.BeginHrDocumentPurge(Now.AddDays(-1));
        purged.PurgeReleasedHrDocument(Now.AddDays(-1));
        var pending = world.Store.Add(NewFile(TenantId, available: false));

        await world.Purge.PurgeAsync(
            TenantId, [product.Id.Value, foreign.Id.Value, purged.Id.Value, pending.Id.Value, Guid.CreateVersion7()], world.MarkProcessed, Ct);

        Assert.Empty(world.Objects.Deleted);
        Assert.Empty(world.Audit.SystemEntries);
        Assert.Empty(world.References.Asked);
        Assert.Equal(FileResourceStatus.Available, product.Status);
        Assert.Equal(FileResourceStatus.Available, foreign.Status);
        Assert.Equal(FileResourceStatus.PendingScan, pending.Status);
        Assert.Equal(1, world.Marks);
        // R1: cada id cierra su transacción, también los saltados; la marca va con el último.
        Assert.Equal(5, world.Store.Saves);
        Assert.Equal(4, world.SavesAtMark);
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

        // La marca ya confirmada queda; la segunda transacción se revierte y el mensaje vuelve cuando vence su reclamo.
        Assert.Equal([FileResourceStatus.Deleted], r2.Store.SavedStatuses[r2File.Id]);
        Assert.Equal(0, r2.Marks);
        Assert.Equal(1, r2.Store.Abandons);
        Assert.Equal(1, r2.Store.Saves);
        Assert.Equal(FileResourceStatus.Deleted, r2File.Status);
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

        Assert.Equal([FileResourceStatus.Deleted, FileResourceStatus.Purged], world.Store.SavedStatuses[first.Id]);
        Assert.Equal([FileResourceStatus.Deleted], world.Store.SavedStatuses[second.Id]);
        Assert.Equal(3, world.Store.Saves);
        Assert.Equal(1, world.Store.Abandons);
        Assert.Equal(0, world.Marks);
    }

    /// <summary>
    /// El candado en memoria: filtra por tenant como el <c>SELECT … FOR UPDATE</c>, cuenta guardados y abandonos y anota el estado de
    /// cada archivo bloqueado en el momento de confirmar. Un abandono no deshace lo que cambió en memoria: lo que vale es
    /// <see cref="SavedStatuses"/>, lo confirmado. <see cref="FailSaveWhen"/> simula un commit que falla (no anota nada) y
    /// <see cref="BeforeLock"/>, otra réplica que actúa entre la marca y el segundo candado.
    /// </summary>
    private sealed class InMemoryPurgeStore : IHrDocumentPurgeStore
    {
        private readonly List<FileResource> _files = [];
        private readonly List<FileResource> _locked = [];
        private int _lockCalls;

        public int Saves { get; private set; }

        public int Abandons { get; private set; }

        public int LocksHeld => _locked.Count;

        public Func<FileResource, bool>? FailSaveWhen { get; set; }

        public Action<int>? BeforeLock { get; set; }

        public Dictionary<FileResourceId, List<FileResourceStatus>> SavedStatuses { get; } = [];

        public FileResource Add(FileResource file)
        {
            _files.Add(file);
            return file;
        }

        public Task<FileResource?> LockAsync(Guid tenantId, FileResourceId id, CancellationToken ct)
        {
            BeforeLock?.Invoke(++_lockCalls);
            var file = _files.SingleOrDefault(candidate => candidate.Id == id && candidate.TenantId == tenantId);
            if (file is not null)
            {
                _locked.Add(file);
            }

            return Task.FromResult(file);
        }

        public Task SaveAsync(CancellationToken ct)
        {
            if (FailSaveWhen is { } fail && _locked.Any(fail))
            {
                throw new InvalidOperationException("The commit failed.");
            }

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

        public List<int> SavesWhenAsked { get; } = [];

        public void Respond(params Guid[] referenced) => _responses.Enqueue(referenced.ToHashSet());

        public Task<IReadOnlySet<Guid>> FindReferencedAsync(Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
        {
            if (Failing)
            {
                throw new InvalidOperationException("Hr is down.");
            }

            Asked.Add([.. fileIds]);
            LocksHeldWhenAsked.Add(Store!.LocksHeld);
            SavesWhenAsked.Add(Store.Saves);
            return Task.FromResult(_responses.TryDequeue(out var response) ? response : new HashSet<Guid>());
        }
    }

    /// <summary>El bucket privado: anota cada clave borrada (y cuántos guardados había), puede fallar en una y responde
    /// <c>StatAsync</c> nulo para lo que falta o ya se borró.</summary>
    private sealed class RecordingObjectStorage : IObjectStorage
    {
        public InMemoryPurgeStore? Store { get; set; }

        public string? FailingKey { get; set; }

        public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

        public List<string> Deleted { get; } = [];

        public List<int> SavesWhenDeleted { get; } = [];

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            if (string.Equals(key, FailingKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("R2 is down.");
            }

            Deleted.Add(key);
            SavesWhenDeleted.Add(Store!.Saves);
            return Task.CompletedTask;
        }

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Missing.Contains(key) || Deleted.Contains(key) ? null : new StoredObject(1024, "abc"));

        public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Anota nivel y mensaje de cada entrada (calco del de <c>HrDocumentReleaseProcessorTests</c>).</summary>
    private sealed class RecordingLogger : ILogger<HrDocumentPurge>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
