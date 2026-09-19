using BuildingBlocks.Application;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Spec 2026-09-16, D15: borrar o despublicar un comprobante que algún pedido referencia se rechaza sin
/// tocar su copia pública, y publicar un comprobante se rechaza siempre. Un comprobante sin referencia
/// y un archivo User siguen como antes.
/// </summary>
public sealed class PaymentProofFileManagementTests
{
    private const string PublicKey = "payment-proofs/abc.webp";

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeletingAReferencedPaymentProofIsRejectedAndKeepsItsPublicCopy()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();
        var unitOfWork = new CountingStorageUnitOfWork();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            DeleteHandler(proof, storage, unitOfWork, new StubFileReferenceProbe(referenced: true))
                .HandleAsync(new SoftDeleteFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Empty(storage.DeletedKeys);
        Assert.Equal(PublicKey, proof.PublicStorageKey);
        Assert.Equal(FileResourceStatus.Available, proof.Status);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task DeletingAnUnreferencedPaymentProofWorksAsBefore()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();

        var result = await DeleteHandler(
                proof, storage, new CountingStorageUnitOfWork(), new StubFileReferenceProbe(referenced: false))
            .HandleAsync(new SoftDeleteFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.True(result.Deleted);
        Assert.Equal([PublicKey], storage.DeletedKeys);
        Assert.Null(proof.PublicStorageKey);
        Assert.Equal(FileResourceStatus.Deleted, proof.Status);
    }

    [Fact]
    public async Task UnpublishingAReferencedPaymentProofIsRejectedAndKeepsItsPublicCopy()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();
        var unitOfWork = new CountingStorageUnitOfWork();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            UnpublishHandler(proof, storage, unitOfWork, new StubFileReferenceProbe(referenced: true))
                .HandleAsync(new UnpublishFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Empty(storage.DeletedKeys);
        Assert.Equal(PublicKey, proof.PublicStorageKey);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task UnpublishingAnUnreferencedPaymentProofWorksAsBefore()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();

        await UnpublishHandler(
                proof, storage, new CountingStorageUnitOfWork(), new StubFileReferenceProbe(referenced: false))
            .HandleAsync(new UnpublishFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken);

        Assert.Equal([PublicKey], storage.DeletedKeys);
        Assert.Null(proof.PublicStorageKey);
    }

    // Fix round 1 (I1): sin sondas registradas no hay forma de saber si un pedido lo referencia.
    // Fallar cerrado, no abierto.
    [Fact]
    public async Task DeletingAPaymentProofWithNoProbesRegisteredIsRejected()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();
        var unitOfWork = new CountingStorageUnitOfWork();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            DeleteHandler(proof, storage, unitOfWork)
                .HandleAsync(new SoftDeleteFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Empty(storage.DeletedKeys);
        Assert.Equal(PublicKey, proof.PublicStorageKey);
        Assert.Equal(FileResourceStatus.Available, proof.Status);
        Assert.Equal(0, unitOfWork.Saves);
    }

    [Fact]
    public async Task UnpublishingAPaymentProofWithNoProbesRegisteredIsRejected()
    {
        var proof = MovedPaymentProof();
        var storage = new RecordingPublicObjectStorage();
        var unitOfWork = new CountingStorageUnitOfWork();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            UnpublishHandler(proof, storage, unitOfWork)
                .HandleAsync(new UnpublishFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Empty(storage.DeletedKeys);
        Assert.Equal(PublicKey, proof.PublicStorageKey);
        Assert.Equal(0, unitOfWork.Saves);
    }

    // Un comprobante sólo llega al público por el movimiento (sección 2). Una imagen, para que el
    // rechazo no sea el de «sólo imágenes» de FileResource.Publish.
    [Fact]
    public async Task PublishingAPaymentProofIsAlwaysRejected()
    {
        var proof = AvailablePaymentProof();
        var storage = new RecordingPublicObjectStorage();

        var error = await Assert.ThrowsAsync<StorageDomainException>(() =>
            new PublishFileHandler(
                    new InMemoryFileResourceRepository(proof),
                    new CountingStorageUnitOfWork(),
                    new FilePublication(storage, new FixedClock(Now)),
                    storage,
                    new RecordingStorageAuditPublisher(),
                    new AllowAllExecutionContext(TenantId),
                    new FixedClock(Now))
                .HandleAsync(new PublishFileCommand(TenantId, proof.Id.Value), TestContext.Current.CancellationToken));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Empty(storage.Copies);
        Assert.Null(proof.PublicStorageKey);
    }

    // D13: la guarda es sólo para comprobantes. A una imagen User no se le pregunta a ninguna sonda.
    [Fact]
    public async Task AUserImageIsDeletedWithoutAskingTheProbes()
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User,
            "producto.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/producto", Now);
        image.CompleteUpload("checksum", 2048, Now);
        image.Promote($"files/tenants/{TenantId:N}/producto", Now);
        var imageKey = $"tenants/{TenantId:N}/media/producto/original.png";
        image.Publish(imageKey, Now);
        var storage = new RecordingPublicObjectStorage();
        var probe = new StubFileReferenceProbe(referenced: true);

        var result = await DeleteHandler(image, storage, new CountingStorageUnitOfWork(), probe)
            .HandleAsync(new SoftDeleteFileCommand(TenantId, image.Id.Value), TestContext.Current.CancellationToken);

        Assert.True(result.Deleted);
        Assert.Equal([imageKey], storage.DeletedKeys);
        Assert.Empty(probe.Asked);
    }

    // Fix round 1 (I1): la guarda de "sin sondas" es sólo para PaymentProof; un archivo User se borra
    // igual que siempre, sin que le importe si hay sondas registradas.
    [Fact]
    public async Task AUserImageIsDeletedWithNoProbesRegistered()
    {
        var image = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User,
            "producto.png", "image/png", 2048, $"staging/tenants/{TenantId:N}/producto", Now);
        image.CompleteUpload("checksum", 2048, Now);
        image.Promote($"files/tenants/{TenantId:N}/producto", Now);
        var imageKey = $"tenants/{TenantId:N}/media/producto/original.png";
        image.Publish(imageKey, Now);
        var storage = new RecordingPublicObjectStorage();

        var result = await DeleteHandler(image, storage, new CountingStorageUnitOfWork())
            .HandleAsync(new SoftDeleteFileCommand(TenantId, image.Id.Value), TestContext.Current.CancellationToken);

        Assert.True(result.Deleted);
        Assert.Equal([imageKey], storage.DeletedKeys);
    }

    private static FileResource AvailablePaymentProof()
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.PaymentProof,
            "comprobante.webp", "image/webp", 2048, $"staging/tenants/{TenantId:N}/comprobante", Now);
        proof.CompleteUpload("checksum", 2048, Now);
        proof.MarkClean(Now);
        return proof;
    }

    private static FileResource MovedPaymentProof()
    {
        var proof = AvailablePaymentProof();
        proof.MoveToPublic(PublicKey, Now);
        return proof;
    }

    private static SoftDeleteFileHandler DeleteHandler(
        FileResource resource,
        RecordingPublicObjectStorage storage,
        CountingStorageUnitOfWork unitOfWork,
        params IFileReferenceProbe[] probes) =>
        new(
            new InMemoryFileResourceRepository(resource),
            unitOfWork,
            new FilePublication(storage, new FixedClock(Now)),
            probes,
            new RecordingStorageAuditPublisher(),
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));

    private static UnpublishFileHandler UnpublishHandler(
        FileResource resource,
        RecordingPublicObjectStorage storage,
        CountingStorageUnitOfWork unitOfWork,
        params IFileReferenceProbe[] probes) =>
        new(
            new InMemoryFileResourceRepository(resource),
            unitOfWork,
            new FilePublication(storage, new FixedClock(Now)),
            storage,
            probes,
            new RecordingStorageAuditPublisher(),
            new AllowAllExecutionContext(TenantId),
            new FixedClock(Now));
}
