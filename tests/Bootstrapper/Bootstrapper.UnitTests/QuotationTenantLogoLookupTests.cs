using Microsoft.Extensions.Logging.Abstractions;
using Modules.Quotations.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Cómo ve Quotations el logo vigente de un tenant: une `Tenancy.LogoFileId` con el
/// `FileResource` de Storage (decisión 9 del spec 2026-09-19). `null` sin fallar en cualquier
/// caso donde el PDF no debería reventar por un logo mal formado: sin logo asignado, con el
/// archivo ya no disponible, o con un tipo que `TenantLogoStorage` no debería haber dejado
/// asignar.
/// </summary>
public sealed class QuotationTenantLogoLookupTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ATenantWithoutALogoReturnsNull()
    {
        var found = await LookupWith(tenantDirectory: new FakeTenantDirectory(logoFileId: null))
            .FindAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Null(found);
    }

    [Fact]
    public async Task AFileThatIsNotAvailableReturnsNull()
    {
        // PendingScan y no Available: el escaneo antivirus todavía no lo marcó limpio.
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, TenantId, FileOwnerType.Tenant, "logo",
            "image/png", 1024, $"files/tenants/{TenantId:N}/logo", Now);
        file.CompleteUpload("checksum", 1024, Now);

        var found = await LookupWith(file).FindAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Null(found);
    }

    // Tenancy apunta a un archivo que Storage ya no tiene: el PDF sale sin logo.
    [Fact]
    public async Task AFileMissingFromTheRepositoryReturnsNull()
    {
        var found = await LookupWith(new FakeTenantDirectory(Guid.CreateVersion7()))
            .FindAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Null(found);
    }

    // Revalidar el tenant en la frontera: aunque Tenancy apunte a él, un archivo de otro tenant
    // nunca se imprime en este PDF.
    [Fact]
    public async Task AnotherTenantsFileReturnsNull()
    {
        var otherTenantId = Guid.CreateVersion7();
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), otherTenantId, otherTenantId, FileOwnerType.Tenant, "logo",
            "image/png", 1024, $"files/tenants/{otherTenantId:N}/logo", Now);
        file.CompleteUpload("checksum", 1024, Now);
        file.MarkClean(Now);

        var found = await LookupWith(file).FindAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Null(found);
    }

    // Un tipo que TenantLogoStorage no debería haber dejado asignar (spec 2026-09-19): el PDF
    // sale sin logo en vez de fallar por un mapeo de extensión que no existe.
    [Fact]
    public async Task AnUnmappedMimeTypeReturnsNull()
    {
        var file = AvailableLogo("image/gif");

        var found = await LookupWith(file).FindAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Null(found);
    }

    [Fact]
    public async Task APngLogoResolvesToLogoPng()
    {
        var file = AvailableLogo("image/png");

        var found = await LookupWith(file).FindAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(".png", found.Extension);
        Assert.Equal(file.Id.Value, found.FileId);
        Assert.Equal(file.StorageKey, found.StorageKey);
    }

    [Fact]
    public async Task AJpegLogoResolvesToLogoJpg()
    {
        var file = AvailableLogo("image/jpeg");

        var found = await LookupWith(file).FindAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(".jpg", found.Extension);
    }

    [Fact]
    public async Task AWebpLogoResolvesToLogoWebp()
    {
        var file = AvailableLogo("image/webp");

        var found = await LookupWith(file).FindAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(".webp", found.Extension);
    }

    // FindAsync sólo decide si el PDF sigue vigente (spec 2026-09-19, decisión 9): bajar los
    // bytes es responsabilidad de ReadAsync, y sólo al regenerar.
    [Fact]
    public async Task FindAsyncDoesNotDownloadTheFile()
    {
        var file = AvailableLogo("image/png");
        var storage = new UnusedObjectStorage();

        await new QuotationTenantLogoLookup(
            new FakeTenantDirectory(file.Id.Value), new InMemoryFileResourceRepository(file), storage,
            NullLogger<QuotationTenantLogoLookup>.Instance)
            .FindAsync(TenantId, TestContext.Current.CancellationToken);

        Assert.Equal(0, storage.DownloadCalls);
    }

    // Un logo que no se puede bajar no bloquea ni el export ni el envío por WhatsApp: ReadAsync
    // devuelve null y el PDF sale sin logo.
    [Fact]
    public async Task ReadAsyncReturnsNullWhenTheDownloadFails()
    {
        var storage = new UnusedObjectStorage { DownloadFailure = new InvalidOperationException("R2 down") };
        var lookup = new QuotationTenantLogoLookup(
            new FakeTenantDirectory(null), new InMemoryFileResourceRepository(), storage,
            NullLogger<QuotationTenantLogoLookup>.Instance);

        var content = await lookup.ReadAsync(
            new QuotationLogoRef(Guid.CreateVersion7(), "files/tenants/x/logo", ".png"),
            TestContext.Current.CancellationToken);

        Assert.Null(content);
        Assert.Equal(1, storage.DownloadCalls);
    }

    // Una cancelación no es un logo ilegible: se propaga para que el request se corte.
    [Fact]
    public async Task ReadAsyncPropagatesCancellation()
    {
        var storage = new UnusedObjectStorage { DownloadFailure = new OperationCanceledException() };
        var lookup = new QuotationTenantLogoLookup(
            new FakeTenantDirectory(null), new InMemoryFileResourceRepository(), storage,
            NullLogger<QuotationTenantLogoLookup>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => lookup.ReadAsync(
            new QuotationLogoRef(Guid.CreateVersion7(), "files/tenants/x/logo", ".png"),
            TestContext.Current.CancellationToken));
    }

    private static FileResource AvailableLogo(string mimeType)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, TenantId, FileOwnerType.Tenant, "logo",
            mimeType, 1024, $"files/tenants/{TenantId:N}/logo", Now);
        file.CompleteUpload("checksum", 1024, Now);
        file.MarkClean(Now);
        return file;
    }

    private static QuotationTenantLogoLookup LookupWith(FileResource file) =>
        LookupWith(new FakeTenantDirectory(file.Id.Value), file);

    private static QuotationTenantLogoLookup LookupWith(
        FakeTenantDirectory tenantDirectory, FileResource? file = null) =>
        new(
            tenantDirectory,
            file is null ? new InMemoryFileResourceRepository() : new InMemoryFileResourceRepository(file),
            new UnusedObjectStorage(),
            NullLogger<QuotationTenantLogoLookup>.Instance);

    /// <summary>El único dato que <see cref="QuotationTenantLogoLookup"/> necesita de Tenancy: el
    /// <c>LogoFileId</c> vigente del tenant, o null sin logo.</summary>
    private sealed class FakeTenantDirectory(Guid? logoFileId) : ITenantDirectory
    {
        public Task<string?> GetSlugAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetDisplayNameAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetTimeZoneAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> GetLogoFileIdAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(logoFileId);
    }

    // Anota si alguien llamó a DownloadAsync; el resto de la interfaz no lo necesita este puerto.
    private sealed class UnusedObjectStorage : IObjectStorage
    {
        public int DownloadCalls { get; private set; }

        /// <summary>Si no es null, <see cref="DownloadAsync"/> la lanza.</summary>
        public Exception? DownloadFailure { get; init; }

        public Task<Uri> CreatePresignedUploadUrlAsync(
            string key, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            return DownloadFailure is null
                ? Task.FromResult<byte[]>([])
                : Task.FromException<byte[]>(DownloadFailure);
        }

        public Task UploadAsync(
            string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
