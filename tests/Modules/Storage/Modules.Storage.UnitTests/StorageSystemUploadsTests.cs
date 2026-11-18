using System.Security.Cryptography;
using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Soporte fase 3 (plan F30 y F31): la subida sin miembro. La sesión la ata el par <c>(ownerType, ownerId)</c> que recibe quien la
/// abrió; completar pasa por el mismo núcleo que el handler de la consola (tamaño, magic bytes, antivirus, variantes, promoción) y la
/// auditoría sale con actor sistema. El handler de la consola conserva su permiso.
/// </summary>
public sealed class StorageSystemUploadsTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] PdfBytes = "%PDF-1.7\nadjunto"u8.ToArray();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpenValidatesTheDeclarationWithTheGivenMax()
    {
        var fixture = new Fixture();
        const long max = 10 * 1024 * 1024;

        var tooLarge = await Assert.ThrowsAsync<StorageDomainException>(() => fixture.Uploads.OpenAsync(
            TenantId, FileOwnerType.SupportTicket, Guid.NewGuid(), "acta.pdf", "application/pdf", max + 1, max, Ct));
        var wrongType = await Assert.ThrowsAsync<StorageDomainException>(() => fixture.Uploads.OpenAsync(
            TenantId, FileOwnerType.SupportTicket, Guid.NewGuid(), "script.exe", "application/octet-stream", 10, max, Ct));
        var session = await fixture.Uploads.OpenAsync(
            TenantId, FileOwnerType.SupportTicket, Guid.NewGuid(), "acta.pdf", "application/pdf", max, max, Ct);

        Assert.Equal("storage.file.size_invalid", tooLarge.Code);
        Assert.Equal("storage.file.type_not_allowed", wrongType.Code);
        Assert.Equal("application/pdf", session.Headers["Content-Type"]);
        Assert.Equal("*", session.Headers["If-None-Match"]);
        var stored = Assert.Single(fixture.Repository.Items);
        Assert.Equal(session.FileId, stored.Id.Value);
        Assert.Equal(FileResourceStatus.PendingUpload, stored.Status);
        Assert.Equal(FileOwnerType.SupportTicket, stored.OwnerType);
    }

    [Fact]
    public async Task OpenRejectsAMaxAboveTheStoragePolicy()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Uploads.OpenAsync(
            TenantId, FileOwnerType.SupportTicket, Guid.NewGuid(), "acta.pdf", "application/pdf", 10,
            FileUploadPolicy.MaxSizeBytes + 1, Ct));
    }

    [Fact]
    public async Task CompleteWithAnotherOwnerIdIsNotFound()
    {
        var fixture = new Fixture();
        var ownerId = Guid.NewGuid();
        var session = await fixture.OpenAndUploadAsync(ownerId, PdfBytes);

        var anotherOwner = await fixture.Uploads.CompleteAsync(
            TenantId, session.FileId, FileOwnerType.SupportTicket, Guid.NewGuid(), Ct);
        var anotherTenant = await fixture.Uploads.CompleteAsync(
            Guid.CreateVersion7(), session.FileId, FileOwnerType.SupportTicket, ownerId, Ct);
        var anotherType = await fixture.Uploads.CompleteAsync(
            TenantId, session.FileId, FileOwnerType.User, ownerId, Ct);

        Assert.Equal(SystemUploadOutcome.NotFound, anotherOwner.Outcome);
        Assert.Equal(SystemUploadOutcome.NotFound, anotherTenant.Outcome);
        Assert.Equal(SystemUploadOutcome.NotFound, anotherType.Outcome);
        Assert.Equal(FileResourceStatus.PendingUpload, Assert.Single(fixture.Repository.Items).Status);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task CompleteOfAMismatchedContentIsRejected()
    {
        var fixture = new Fixture(contentMatches: false);
        var ownerId = Guid.NewGuid();
        var session = await fixture.OpenAndUploadAsync(ownerId, PdfBytes);

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.SupportTicket, ownerId, Ct);

        Assert.Equal(SystemUploadOutcome.Rejected, result.Outcome);
        Assert.Equal(FileResourceStatus.Quarantined, Assert.Single(fixture.Repository.Items).Status);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(("storage.file.rejected", "content_type_mismatch", true), (audit.Action, audit.Outcome, audit.System));
    }

    [Fact]
    public async Task CompleteOfAMissingObjectSaysSo()
    {
        var fixture = new Fixture();
        var ownerId = Guid.NewGuid();
        var session = await fixture.Uploads.OpenAsync(
            TenantId, FileOwnerType.SupportTicket, ownerId, "acta.pdf", "application/pdf", PdfBytes.Length, 1024, Ct);

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.SupportTicket, ownerId, Ct);

        Assert.Equal(SystemUploadOutcome.ObjectMissing, result.Outcome);
        Assert.Equal(FileResourceStatus.PendingUpload, Assert.Single(fixture.Repository.Items).Status);
    }

    [Fact]
    public async Task CompleteOfAnInfectedFileIsRejected()
    {
        var fixture = new Fixture(verdict: FileScanResult.Infected);
        var ownerId = Guid.NewGuid();
        var session = await fixture.OpenAndUploadAsync(ownerId, PdfBytes);

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.SupportTicket, ownerId, Ct);

        Assert.Equal(SystemUploadOutcome.Rejected, result.Outcome);
        Assert.Equal(FileResourceStatus.Quarantined, Assert.Single(fixture.Repository.Items).Status);
    }

    [Fact]
    public async Task CompleteOfACleanFilePromotesIt()
    {
        var fixture = new Fixture();
        var ownerId = Guid.NewGuid();
        var session = await fixture.OpenAndUploadAsync(ownerId, PdfBytes);
        var stagingKey = Assert.Single(fixture.Storage.Keys);

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.SupportTicket, ownerId, Ct);
        var again = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.SupportTicket, ownerId, Ct);

        Assert.Equal(SystemUploadOutcome.Available, result.Outcome);
        Assert.Equal(SystemUploadOutcome.Available, again.Outcome);
        var resource = Assert.Single(fixture.Repository.Items);
        Assert.Equal(FileResourceStatus.Available, resource.Status);
        Assert.StartsWith("files/", resource.StorageKey, StringComparison.Ordinal);
        Assert.DoesNotContain(stagingKey, fixture.Storage.Keys);
        Assert.Contains(resource.StorageKey, fixture.Storage.Keys);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(("storage.file.uploaded", "success", true), (audit.Action, audit.Outcome, audit.System));
    }

    [Fact]
    public async Task FindAvailableIgnoresPendingAndQuarantined()
    {
        var fixture = new Fixture();
        var cleanOwner = Guid.NewGuid();
        var clean = await fixture.OpenAndUploadAsync(cleanOwner, PdfBytes);
        await fixture.Uploads.CompleteAsync(TenantId, clean.FileId, FileOwnerType.SupportTicket, cleanOwner, Ct);
        var pendingOwner = Guid.NewGuid();
        var pending = await fixture.OpenAndUploadAsync(pendingOwner, PdfBytes);

        var rejectedFixture = new Fixture(contentMatches: false);
        var rejectedOwner = Guid.NewGuid();
        var rejected = await rejectedFixture.OpenAndUploadAsync(rejectedOwner, PdfBytes);
        await rejectedFixture.Uploads.CompleteAsync(TenantId, rejected.FileId, FileOwnerType.SupportTicket, rejectedOwner, Ct);

        var found = await fixture.Uploads.FindAvailableAsync(TenantId, clean.FileId, FileOwnerType.SupportTicket, cleanOwner, Ct);

        Assert.NotNull(found);
        Assert.Equal((clean.FileId, "acta.pdf", "application/pdf", (long)PdfBytes.Length), (found.FileId, found.Name, found.MimeType, found.SizeBytes));
        Assert.Null(await fixture.Uploads.FindAvailableAsync(TenantId, pending.FileId, FileOwnerType.SupportTicket, pendingOwner, Ct));
        Assert.Null(await rejectedFixture.Uploads.FindAvailableAsync(
            TenantId, rejected.FileId, FileOwnerType.SupportTicket, rejectedOwner, Ct));
        Assert.Null(await fixture.Uploads.FindAvailableAsync(TenantId, clean.FileId, FileOwnerType.SupportTicket, Guid.NewGuid(), Ct));
        Assert.Null(await fixture.Uploads.FindAvailableAsync(
            Guid.CreateVersion7(), clean.FileId, FileOwnerType.SupportTicket, cleanOwner, Ct));
    }

    [Fact]
    public async Task TheDownloadUrlIsOnlyForAnAvailableFileOfTheTenant()
    {
        var fixture = new Fixture();
        var ownerId = Guid.NewGuid();
        var session = await fixture.OpenAndUploadAsync(ownerId, PdfBytes);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => fixture.Uploads.CreateDownloadUrlAsync(TenantId, session.FileId, Ct));
        await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.SupportTicket, ownerId, Ct);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            fixture.Uploads.CreateDownloadUrlAsync(Guid.CreateVersion7(), session.FileId, Ct));
        var url = await fixture.Uploads.CreateDownloadUrlAsync(TenantId, session.FileId, Ct);

        Assert.Equal(TimeSpan.FromMinutes(5), fixture.Storage.LastDownloadExpiry);
        Assert.Equal("acta.pdf", fixture.Storage.LastDownloadName);
        Assert.Contains(Assert.Single(fixture.Repository.Items).StorageKey, url.AbsoluteUri, StringComparison.Ordinal);
    }

    // El camino sin miembro no afloja al de la consola: sin storage.file.upload, o con el permiso en otro tenant, el handler sigue
    // respondiendo 403 antes de tocar el archivo.
    [Fact]
    public async Task TheMemberCompletionStillRequiresThePermission()
    {
        var fixture = new Fixture();
        var ownerId = Guid.NewGuid();
        var session = await fixture.OpenAndUploadAsync(ownerId, PdfBytes);

        var withoutPermission = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            fixture.MemberHandler(new PermissionContext(TenantId, granted: false))
                .HandleAsync(new CompleteUploadCommand(TenantId, session.FileId), Ct));
        var otherTenant = await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            fixture.MemberHandler(new PermissionContext(Guid.CreateVersion7(), granted: true))
                .HandleAsync(new CompleteUploadCommand(TenantId, session.FileId), Ct));

        Assert.Equal("authorization.denied", withoutPermission.Code);
        Assert.Equal("authorization.denied", otherTenant.Code);
        Assert.Equal(FileResourceStatus.PendingUpload, Assert.Single(fixture.Repository.Items).Status);
        Assert.Empty(fixture.Audit.Entries);

        // Fix round 1 del Bloque D: con el permiso tampoco lo completa. Un archivo SupportTicket no existe para la consola.
        var withPermission = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            fixture.MemberHandler(new PermissionContext(TenantId, granted: true))
                .HandleAsync(new CompleteUploadCommand(TenantId, session.FileId), Ct));
        Assert.Equal("storage.file.not_found", withPermission.Code);
        Assert.Equal(FileResourceStatus.PendingUpload, Assert.Single(fixture.Repository.Items).Status);
    }

    // Revisión final (triage 11): el núcleo se movió a UploadCompletion con un actor anulable. Un miembro que completa un archivo normal
    // de la consola queda auditado a su nombre, nunca como el sistema.
    [Fact]
    public async Task AMemberCompletionIsAuditedAsTheMemberNotTheSystem()
    {
        var fixture = new Fixture();
        var fileId = fixture.SeedConsoleUpload(PdfBytes);

        await fixture.MemberHandler(new PermissionContext(TenantId, granted: true))
            .HandleAsync(new CompleteUploadCommand(TenantId, fileId), Ct);

        Assert.Equal(FileResourceStatus.Available, Assert.Single(fixture.Repository.Items).Status);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(("storage.file.uploaded", "success", false), (audit.Action, audit.Outcome, audit.System));
    }

    // Productos desde Shopify (spec 2026-11-17 P1): la imagen de un producto la sube el servidor, sin navegador ni URL firmada, por
    // el mismo pipeline. Queda Available en files/, auditada como sistema.
    [Fact]
    public async Task StoringAProductImageLeavesItAvailable()
    {
        var fixture = new Fixture();
        var productId = Guid.CreateVersion7();

        var stored = await fixture.Uploads.StoreAsync(
            TenantId, FileOwnerType.Product, productId, " foto.jpg ", "image/jpeg", PdfBytes, 10 * 1024 * 1024, Ct);

        Assert.Equal(SystemUploadOutcome.Available, stored.Outcome);
        var resource = Assert.Single(fixture.Repository.Items);
        Assert.Equal(stored.FileId, resource.Id.Value);
        Assert.Equal((FileResourceStatus.Available, FileOwnerType.Product, productId, "foto.jpg"),
            (resource.Status, resource.OwnerType, resource.OwnerId, resource.Name));
        Assert.StartsWith("files/", resource.StorageKey, StringComparison.Ordinal);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(("storage.file.uploaded", "success", true), (audit.Action, audit.Outcome, audit.System));
    }

    [Fact]
    public async Task StoringAMismatchedContentIsRejected()
    {
        var fixture = new Fixture(contentMatches: false);

        var stored = await fixture.Uploads.StoreAsync(
            TenantId, FileOwnerType.Product, Guid.CreateVersion7(), "foto.jpg", "image/jpeg", PdfBytes, 10 * 1024 * 1024, Ct);

        Assert.Equal(SystemUploadOutcome.Rejected, stored.Outcome);
        Assert.Equal(FileResourceStatus.Quarantined, Assert.Single(fixture.Repository.Items).Status);
    }

    [Fact]
    public async Task StoringMoreThanTheGivenMaxIsRejectedBeforeAnything()
    {
        var fixture = new Fixture();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() => fixture.Uploads.StoreAsync(
            TenantId, FileOwnerType.Product, Guid.CreateVersion7(), "foto.jpg", "image/jpeg", PdfBytes, PdfBytes.Length - 1, Ct));

        Assert.Equal("storage.file.size_invalid", error.Code);
        Assert.Empty(fixture.Repository.Items);
        Assert.Empty(fixture.Storage.Keys);
    }

    // La subida de servidor es para dueños que la consola ve; los del sistema tienen su propio camino con secreto (OpenAsync).
    [Fact]
    public async Task StoringASystemOwnedTypeThrows()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Uploads.StoreAsync(
            TenantId, FileOwnerType.SupportTicket, Guid.NewGuid(), "acta.pdf", "application/pdf", PdfBytes, 1024, Ct));

        Assert.Empty(fixture.Repository.Items);
    }

    // Revisión final (triage 13): el puerto sin miembro sólo firma archivos de un dueño del sistema (SystemOwnedFiles). Un archivo
    // disponible de la consola, del mismo tenant, es 404 como uno que no existe.
    [Fact]
    public async Task TheDownloadUrlIsNotForAFileOfTheConsole()
    {
        var fixture = new Fixture();
        var fileId = fixture.SeedConsoleUpload(PdfBytes);
        await fixture.MemberHandler(new PermissionContext(TenantId, granted: true))
            .HandleAsync(new CompleteUploadCommand(TenantId, fileId), Ct);
        Assert.Equal(FileResourceStatus.Available, Assert.Single(fixture.Repository.Items).Status);

        var notFound = await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            fixture.Uploads.CreateDownloadUrlAsync(TenantId, fileId, Ct));

        Assert.Equal("storage.file.not_found", notFound.Code);
        Assert.Null(fixture.Storage.LastDownloadName);
    }

    // Spec 2026-11-18 (multimedia del chatbot): el inspector de la consola no conoce audio ni video. El contenido de un ChatbotMedia lo
    // revisa el sniffer de Mensajería por el puerto, con la cabecera (64 bytes) y el nombre.
    [Fact]
    public async Task AChatbotMediaMp3IsCheckedByTheMediaSnifferNotTheConsoleInspector()
    {
        var fixture = new Fixture(contentMatches: false);
        var content = Bytes(100, fill: 0x49);
        var session = await fixture.OpenChatbotMediaAsync("jingle.mp3", "audio/mpeg", content);

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.ChatbotMedia, TenantId, Ct);

        Assert.Equal(SystemUploadOutcome.Available, result.Outcome);
        Assert.Equal(("jingle.mp3", 64), Assert.Single(fixture.Media.Checked));
    }

    [Fact]
    public async Task AChatbotMediaFileWhoseHeaderDoesNotMatchIsRejected()
    {
        var fixture = new Fixture();
        fixture.Media.Matching = false;
        var session = await fixture.OpenChatbotMediaAsync("jingle.mp3", "audio/mpeg", Bytes(10, fill: 0x01));

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.ChatbotMedia, TenantId, Ct);

        Assert.Equal(SystemUploadOutcome.Rejected, result.Outcome);
        Assert.Equal(FileResourceStatus.Quarantined, Assert.Single(fixture.Repository.Items).Status);
        Assert.Equal(("jingle.mp3", 10), Assert.Single(fixture.Media.Checked));
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(("storage.file.rejected", "content_type_mismatch", true), (audit.Action, audit.Outcome, audit.System));
    }

    // Meta no baja por link una imagen de más de 5 MB: se comprime antes de promover, y lo que queda en files/ es lo comprimido.
    [Fact]
    public async Task ASixMegabyteChatbotImageIsShrunkBeforePromotion()
    {
        var fixture = new Fixture();
        var shrunk = Bytes(2048, fill: 0x7F);
        fixture.Media.Shrunk = new ProcessedChatbotImage(shrunk, "image/jpeg", ".jpg");
        var session = await fixture.OpenChatbotMediaAsync("catalogo.png", "image/png", Bytes(6 * 1024 * 1024, fill: 0x10));

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.ChatbotMedia, TenantId, Ct);

        Assert.Equal(SystemUploadOutcome.Available, result.Outcome);
        var resource = Assert.Single(fixture.Repository.Items);
        Assert.Equal(("catalogo.jpg", "image/jpeg", (long)shrunk.Length),
            (resource.Name, resource.MimeType, resource.SizeBytes));
        Assert.StartsWith("files/", resource.StorageKey, StringComparison.Ordinal);
        Assert.Equal(shrunk, await fixture.Storage.DownloadAsync(resource.StorageKey, Ct));
        Assert.Equal(1, fixture.Media.ShrinkCalls);
    }

    [Fact]
    public async Task AFourMegabyteChatbotImageIsKeptAsIs()
    {
        var fixture = new Fixture();
        var content = Bytes(4 * 1024 * 1024, fill: 0x10);
        var session = await fixture.OpenChatbotMediaAsync("catalogo.png", "image/png", content);

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.ChatbotMedia, TenantId, Ct);

        Assert.Equal(SystemUploadOutcome.Available, result.Outcome);
        var resource = Assert.Single(fixture.Repository.Items);
        Assert.Equal(("catalogo.png", "image/png", (long)content.Length), (resource.Name, resource.MimeType, resource.SizeBytes));
        Assert.Equal(content, await fixture.Storage.DownloadAsync(resource.StorageKey, Ct));
        Assert.Equal(0, fixture.Media.ShrinkCalls);
    }

    [Fact]
    public async Task AnUnreadableLargeChatbotImageIsRejected()
    {
        var fixture = new Fixture();
        fixture.Media.Shrunk = null;
        var session = await fixture.OpenChatbotMediaAsync("catalogo.jpg", "image/jpeg", Bytes(6 * 1024 * 1024, fill: 0x10));

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.ChatbotMedia, TenantId, Ct);

        Assert.Equal(SystemUploadOutcome.Rejected, result.Outcome);
        Assert.Equal(FileResourceStatus.Quarantined, Assert.Single(fixture.Repository.Items).Status);
        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal(("storage.file.rejected", "image_processing_failed", true), (audit.Action, audit.Outcome, audit.System));
    }

    // El tope guardado de un ChatbotMedia es el de su dueño (100 MB), no los 25 MB de la consola.
    [Fact]
    public async Task AFortyMegabyteChatbotPdfPassesTheStoredSizeCap()
    {
        var fixture = new Fixture();
        var session = await fixture.OpenChatbotMediaAsync("lista.pdf", "application/pdf", Bytes(40 * 1024 * 1024, fill: 0x25));

        var result = await fixture.Uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.ChatbotMedia, TenantId, Ct);

        Assert.Equal(SystemUploadOutcome.Available, result.Outcome);
        Assert.Equal(0, fixture.Media.ShrinkCalls);
    }

    private static byte[] Bytes(int length, byte fill)
    {
        var content = new byte[length];
        Array.Fill(content, fill);
        return content;
    }

    private sealed class Fixture
    {
        public Fixture(bool contentMatches = true, FileScanResult verdict = FileScanResult.Clean)
        {
            Inspector = new FixedContentInspector(contentMatches);
            Scanner = new FixedScanner(verdict);
            Uploads = new StorageSystemUploads(
                Repository, FixedTenantModules.Simulated, Storage, Inspector, new NoVariants(), new NoProofProcessing(), Media,
                Scanner, UnitOfWork, Audit, new FixedClock(Now));
        }

        public FakeChatbotMediaProcessing Media { get; } = new();

        public ListFileResourceRepository Repository { get; } = new();

        public BytesObjectStorage Storage { get; } = new();

        public CountingStorageUnitOfWork UnitOfWork { get; } = new();

        public ActorAuditPublisher Audit { get; } = new();

        public FixedContentInspector Inspector { get; }

        public FixedScanner Scanner { get; }

        public StorageSystemUploads Uploads { get; }

        public async Task<SystemUploadSession> OpenAndUploadAsync(Guid ownerId, byte[] content)
        {
            var session = await Uploads.OpenAsync(
                TenantId, FileOwnerType.SupportTicket, ownerId, "acta.pdf", "application/pdf", content.Length, 1024, Ct);
            Storage.Put(Repository.Items.Single(item => item.Id.Value == session.FileId).StorageKey, content);
            return session;
        }

        /// <summary>Una sesión de multimedia de chatbot (dueño el tenant, tope de su dueño) con el contenido ya «subido».</summary>
        public async Task<SystemUploadSession> OpenChatbotMediaAsync(string name, string mimeType, byte[] content)
        {
            var session = await Uploads.OpenAsync(
                TenantId, FileOwnerType.ChatbotMedia, TenantId, name, mimeType, content.Length,
                FileUploadPolicy.MaxSizeBytesFor(FileOwnerType.ChatbotMedia), Ct);
            Storage.Put(Repository.Items.Single(item => item.Id.Value == session.FileId).StorageKey, content);
            return session;
        }

        /// <summary>Un archivo normal de la consola (dueño <c>User</c>), pendiente y ya «subido» al bucket.</summary>
        public Guid SeedConsoleUpload(byte[] content)
        {
            var id = FileResourceId.New();
            var key = $"staging/tenants/{TenantId:N}/{id.Value:N}";
            Repository.Add(FileResource.CreatePendingUpload(
                id, TenantId, Guid.NewGuid(), FileOwnerType.User, "acta.pdf", "application/pdf", content.Length, key, Now));
            Storage.Put(key, content);
            return id.Value;
        }

        public CompleteUploadHandler MemberHandler(IExecutionContext context) =>
            new(Repository, FixedTenantModules.Simulated, Storage, Inspector, new NoVariants(), new NoProofProcessing(), Media,
                Scanner, UnitOfWork, Audit, context, new FixedClock(Now));
    }

    private sealed class PermissionContext(Guid tenantId, bool granted) : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId { get; } = new(tenantId);

        public bool HasPermission(string permission) => granted && permission == StoragePermissions.FileUpload;
    }

    internal sealed class ListFileResourceRepository : IFileResourceRepository
    {
        public List<FileResource> Items { get; } = [];

        public void Add(FileResource resource) => Items.Add(resource);

        public Task<FileResource?> GetAsync(FileResourceId id, CancellationToken cancellationToken) =>
            Task.FromResult(Items.FirstOrDefault(item => item.Id == id));

        public Task<IReadOnlyList<FileResource>> ListByIdsAsync(
            IReadOnlyCollection<FileResourceId> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

    /// <summary>El bucket privado en memoria, con lo último que se firmó para bajar.</summary>
    internal sealed class BytesObjectStorage : IObjectStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Keys => _objects.Keys.ToArray();

        public TimeSpan? LastDownloadExpiry { get; private set; }

        public string? LastDownloadName { get; private set; }

        public void Put(string key, byte[] content) => _objects[key] = content;

        public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://r2.test/{key}?upload"));

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken)
        {
            LastDownloadExpiry = expiry;
            LastDownloadName = downloadFileName;
            return Task.FromResult(new Uri($"https://r2.test/{key}?download"));
        }

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_objects.TryGetValue(key, out var content)
                ? new StoredObject(content.LongLength, Convert.ToHexStringLower(SHA256.HashData(content)))
                : null);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            _objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task PromoteAsync(string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken)
        {
            _objects[destinationKey] = _objects[sourceKey];
            return Task.CompletedTask;
        }

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_objects[key]);

        public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
        {
            _objects[key] = content;
            return Task.CompletedTask;
        }

        public Task UploadAsync(
            string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    internal sealed class ActorAuditPublisher : IStorageAuditPublisher
    {
        public List<(string Action, string Outcome, bool System)> Entries { get; } = [];

        public void Publish(
            Guid tenantId, Guid actorId, string action, string resourceId, string outcome, DateTimeOffset occurredAt) =>
            Entries.Add((action, outcome, false));

        public void PublishSystem(
            Guid? tenantId, string action, string resourceType, string resourceId, string outcome, DateTimeOffset occurredAt) =>
            Entries.Add((action, outcome, true));
    }

    internal sealed class FixedContentInspector(bool matches) : IFileContentInspector
    {
        public bool Matches(string name, string mimeType, byte[] content) => matches;
    }

    internal sealed class FixedScanner(FileScanResult verdict) : IFileScanner
    {
        public Task<FileScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            Task.FromResult(verdict);
    }

    private sealed class NoVariants : IImageVariantGenerator
    {
        public bool Supports(string mimeType) => false;

        public Task<IReadOnlyList<GeneratedFileVariant>> GenerateAsync(byte[] content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>El puerto del sniffer y el compresor de Mensajería: dice si la cabecera casa y, al comprimir, devuelve lo configurado
    /// (<c>null</c> = imagen ilegible).</summary>
    internal sealed class FakeChatbotMediaProcessing : IChatbotMediaProcessing
    {
        public bool Matching { get; set; } = true;

        public ProcessedChatbotImage? Shrunk { get; set; } = new([0xFF, 0xD8, 0xFF], "image/jpeg", ".jpg");

        public List<(string Name, int HeaderLength)> Checked { get; } = [];

        public int ShrinkCalls { get; private set; }

        public bool Matches(string name, ReadOnlySpan<byte> header)
        {
            Checked.Add((name, header.Length));
            return Matching;
        }

        public Task<ProcessedChatbotImage?> ShrinkAsync(byte[] content, CancellationToken cancellationToken)
        {
            ShrinkCalls++;
            return Task.FromResult(Shrunk);
        }
    }

    private sealed class NoProofProcessing : IPaymentProofImageProcessor
    {
        public bool Supports(string mimeType) => false;

        public Task<ProcessedPaymentProofImage> ProcessAsync(byte[] content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
