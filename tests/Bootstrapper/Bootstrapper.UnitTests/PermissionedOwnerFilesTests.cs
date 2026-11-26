using BuildingBlocks.Application;
using Microsoft.Extensions.Options;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Un archivo cuyo dueño pide un permiso extra (<see cref="FileOwnerModules.RequiredPermissionByOwnerType"/>, hoy el export de saldos
/// de WMS) no se referencia desde otro módulo: los adaptadores que reciben un id de archivo del miembro lo tratan como a uno de dueño de
/// sistema. Si no, <c>storage.file.read</c> o un permiso de Catalog o de Pedidos bastaban para llegar a su contenido. Las teorías
/// recorren la tabla, así que un tipo nuevo con permiso entra solo.
/// </summary>
public sealed class PermissionedOwnerFilesTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<FileOwnerType> PermissionedOwnerTypes => [.. FileOwnerModules.RequiredPermissionByOwnerType.Keys];

    [Theory]
    [MemberData(nameof(PermissionedOwnerTypes))]
    public async Task ItIsNotAProductImage(FileOwnerType ownerType)
    {
        var restricted = AvailableFile(ownerType);
        var image = AvailableFile(FileOwnerType.User);
        var lookup = new ProductImageLookup(
            new InMemoryFileResourceRepository(restricted, image), new RecordingPublicObjectStorage());

        var single = await lookup.FindAsync(restricted.Id.Value, TestContext.Current.CancellationToken);
        var many = await lookup.FindManyAsync([restricted.Id.Value, image.Id.Value], TestContext.Current.CancellationToken);

        Assert.Null(single);
        Assert.Equal([image.Id.Value], many.Keys);
    }

    [Theory]
    [MemberData(nameof(PermissionedOwnerTypes))]
    public async Task ItDoesNotExistForQuotations(FileOwnerType ownerType)
    {
        var restricted = AvailableFile(ownerType);
        var image = AvailableFile(FileOwnerType.User);
        // El bucket no firma nada: si el adaptador llegara a pedir la URL, sale otra excepción.
        var lookup = new QuotationFileLookup(
            new InMemoryFileResourceRepository(restricted, image), new UntouchedObjectStorage(),
            Options.Create(new QuotationsOptions()));

        var single = await lookup.FindAsync(TenantId, restricted.Id.Value, TestContext.Current.CancellationToken);
        var many = await lookup.FindManyAsync(
            TenantId, [restricted.Id.Value, image.Id.Value], TestContext.Current.CancellationToken);
        var error = await Record.ExceptionAsync(() => lookup.CreateDownloadUrlAsync(
            TenantId, restricted.Id.Value, "saldos.pdf", TestContext.Current.CancellationToken));

        Assert.Null(single);
        Assert.Equal([image.Id.Value], many.Keys);
        Assert.Equal("storage.file.not_found", Assert.IsType<ResourceNotFoundException>(error).Code);
    }

    [Theory]
    [MemberData(nameof(PermissionedOwnerTypes))]
    public async Task ItIsNeverCopiedToThePublicBucketAsAPaymentProof(FileOwnerType ownerType)
    {
        var restricted = AvailableFile(ownerType);
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(restricted), storage);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            publisher.PublishAsync(TenantId, restricted.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_found", error.Code);
        Assert.Empty(storage.Copies);
    }

    // PDF a propósito: es un tipo que los tres adaptadores aceptarían, así que sólo el dueño lo deja afuera.
    private static FileResource AvailableFile(FileOwnerType ownerType)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, TenantId, ownerType, "saldos.pdf", "application/pdf", 1024,
            $"staging/{Guid.CreateVersion7():N}", Now);
        file.CompleteUpload("checksum", 1024, Now);
        file.MarkClean(Now);
        return file;
    }

    private sealed class UntouchedObjectStorage : IObjectStorage
    {
        private static InvalidOperationException Touched() => new("The private bucket must not be touched.");

        public Task<Uri> CreatePresignedUploadUrlAsync(string key, string contentType, CancellationToken cancellationToken) =>
            throw Touched();

        public Task<Uri> CreatePresignedDownloadUrlAsync(string key, string? downloadFileName, CancellationToken cancellationToken) =>
            throw Touched();

        public Task<Uri> CreatePresignedDownloadUrlAsync(
            string key, TimeSpan expiry, string? downloadFileName, CancellationToken cancellationToken) =>
            throw Touched();

        public Task<StoredObject?> StatAsync(string key, CancellationToken cancellationToken) => throw Touched();

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => throw Touched();

        public Task PromoteAsync(
            string sourceKey, string destinationKey, string expectedChecksum, CancellationToken cancellationToken) =>
            throw Touched();

        public Task<byte[]> DownloadAsync(string key, CancellationToken cancellationToken) => throw Touched();

        public Task UploadAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken) =>
            throw Touched();

        public Task UploadAsync(
            string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken) =>
            throw Touched();

        public Task<StoredObjectStream?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            throw Touched();
    }
}
