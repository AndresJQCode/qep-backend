using Modules.Storage.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// Los comprobantes de pago v2 en el dominio (spec 2026-09-16): el dueño propio (D2), el reemplazo
/// por la imagen procesada al completar la subida (D7, D8), el movimiento al bucket público (D10) y
/// la purga del que nadie adjuntó (D11).
/// </summary>
public sealed class PaymentProofFileResourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    // D2: el valor se persiste por nombre (StorageDbContext), así que el nombre es contrato.
    [Fact]
    public void PaymentProofIsTheFifthOwnerType()
    {
        Assert.Equal(5, (int)FileOwnerType.PaymentProof);
        Assert.Equal("PaymentProof", FileOwnerType.PaymentProof.ToString());
    }

    // D7: tipo, extensión del nombre, tamaño y checksum; la clave de staging no cambia (D4).
    [Fact]
    public void AProcessedImageReplacesTypeNameSizeAndChecksum()
    {
        var proof = PendingScanProof("comprobante.png", "image/png");

        proof.ReplaceContentWithProcessedImage("image/webp", ".webp", "webp-checksum", 1200, Now.AddMinutes(1));

        Assert.Equal("image/webp", proof.MimeType);
        Assert.Equal("comprobante.webp", proof.Name);
        Assert.Equal(1200, proof.SizeBytes);
        Assert.Equal("webp-checksum", proof.Checksum);
        Assert.Equal("staging/tenants/a/b", proof.StorageKey);
        Assert.Equal(FileResourceStatus.PendingScan, proof.Status);
        Assert.Equal(Now.AddMinutes(1), proof.UpdatedAt);
    }

    [Fact]
    public void TheNewExtensionIsNormalized()
    {
        var proof = PendingScanProof("foto.JPG", "image/jpeg");

        proof.ReplaceContentWithProcessedImage("image/webp", "WEBP", "checksum", 900, Now);

        Assert.Equal("foto.webp", proof.Name);
    }

    // El nombre vale hasta 260 caracteres: se recorta la base, nunca la extensión.
    [Fact]
    public void ALongNameIsTrimmedToFitTheNewExtension()
    {
        var proof = PendingScanProof(new string('a', 256) + ".png", "image/png");

        proof.ReplaceContentWithProcessedImage("image/webp", ".webp", "checksum", 900, Now);

        Assert.Equal(new string('a', 255) + ".webp", proof.Name);
    }

    [Fact]
    public void OnlyAPaymentProofCanBeReplaced()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.User,
            "producto.png", "image/png", 4096, "staging/tenants/a/c", Now);
        file.CompleteUpload("checksum", 4096, Now);

        var error = Assert.Throws<StorageDomainException>(() =>
            file.ReplaceContentWithProcessedImage("image/webp", ".webp", "checksum", 900, Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
    }

    [Fact]
    public void ReplacingRequiresPendingScan()
    {
        var proof = AvailableProof("comprobante.png", "image/png");

        var error = Assert.Throws<StorageDomainException>(() =>
            proof.ReplaceContentWithProcessedImage("image/webp", ".webp", "checksum", 900, Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
    }

    [Fact]
    public void AnEmptyProcessedImageIsRejected()
    {
        var proof = PendingScanProof("comprobante.png", "image/png");

        var error = Assert.Throws<StorageDomainException>(() =>
            proof.ReplaceContentWithProcessedImage("image/webp", ".webp", "checksum", 0, Now));

        Assert.Equal("storage.file.empty", error.Code);
    }

    // D1 y D10: un PDF también se mueve, y el objeto de staging sigue siendo su clave privada.
    [Fact]
    public void MoveToPublicRecordsTheKeyAndKeepsTheStagingKey()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");

        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        Assert.Equal("payment-proofs/abc.pdf", proof.PublicStorageKey);
        Assert.Equal(Now, proof.PublishedAt);
        Assert.True(proof.IsPublic);
        Assert.Equal("staging/tenants/a/b", proof.StorageKey);
        Assert.Equal(FileResourceStatus.Available, proof.Status);
    }

    // PaymentProofMoveWorker puede reintentar un mensaje: la segunda vez no cambia nada.
    [Fact]
    public void MoveToPublicIsIdempotentWithTheSameKey()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        proof.MoveToPublic("payment-proofs/abc.pdf", Now.AddHours(1));

        Assert.Equal("payment-proofs/abc.pdf", proof.PublicStorageKey);
        Assert.Equal(Now, proof.PublishedAt);
        Assert.Equal(Now, proof.UpdatedAt);
    }

    [Fact]
    public void MoveToPublicRejectsADifferentKeyOnceMoved()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        var error = Assert.Throws<StorageDomainException>(() =>
            proof.MoveToPublic("payment-proofs/def.pdf", Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Equal("payment-proofs/abc.pdf", proof.PublicStorageKey);
    }

    // D13: un archivo User nunca se mueve.
    [Fact]
    public void MoveToPublicRequiresAPaymentProof()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.User,
            "comprobante.pdf", "application/pdf", 4096, "staging/tenants/a/c", Now);
        file.CompleteUpload("checksum", 4096, Now);
        file.MarkClean(Now);

        var error = Assert.Throws<StorageDomainException>(() =>
            file.MoveToPublic("payment-proofs/abc.pdf", Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Null(file.PublicStorageKey);
    }

    [Fact]
    public void MoveToPublicRequiresAnAvailableResource()
    {
        var proof = PendingScanProof("comprobante.pdf", "application/pdf");

        var error = Assert.Throws<StorageDomainException>(() =>
            proof.MoveToPublic("payment-proofs/abc.pdf", Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
    }

    [Fact]
    public void MoveToPublicRequiresAKey()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");

        var error = Assert.Throws<StorageDomainException>(() => proof.MoveToPublic(" ", Now));

        Assert.Equal("storage.file.public_key_required", error.Code);
    }

    // D11.
    [Fact]
    public void AnUnattachedProofCanBePurged()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");

        proof.PurgeUnattachedPaymentProof(Now);

        Assert.Equal(FileResourceStatus.Purged, proof.Status);
        Assert.Equal(Now, proof.DeletedAt);
    }

    // Uno movido tiene su copia enlazada en un Excel: el barrido nunca lo purga.
    [Fact]
    public void AMovedProofCannotBePurged()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        var error = Assert.Throws<StorageDomainException>(() => proof.PurgeUnattachedPaymentProof(Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Equal(FileResourceStatus.Available, proof.Status);
    }

    [Fact]
    public void PurgingRequiresAPaymentProof()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.User,
            "comprobante.pdf", "application/pdf", 4096, "staging/tenants/a/c", Now);
        file.CompleteUpload("checksum", 4096, Now);
        file.MarkClean(Now);

        var error = Assert.Throws<StorageDomainException>(() => file.PurgeUnattachedPaymentProof(Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
    }

    // D19: un comprobante movido que se reemplaza o se quita de su pedido se purga. Su copia pública era
    // la única, y el procesador ya la borró.
    [Fact]
    public void AMovedProofCanBePurgedWhenItsOrderLetsItGo()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);

        proof.PurgeDetachedPaymentProof(Now.AddHours(1));

        Assert.Equal(FileResourceStatus.Purged, proof.Status);
        Assert.Equal(Now.AddHours(1), proof.DeletedAt);
        Assert.Equal(Now.AddHours(1), proof.UpdatedAt);
        Assert.Equal("payment-proofs/abc.pdf", proof.PublicStorageKey);
    }

    // D19: uno que todavía espera en staging/ también.
    [Fact]
    public void AStagedProofCanBePurgedWhenItsOrderLetsItGo()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");

        proof.PurgeDetachedPaymentProof(Now);

        Assert.Equal(FileResourceStatus.Purged, proof.Status);
        Assert.Equal(Now, proof.DeletedAt);
        Assert.Null(proof.PublicStorageKey);
    }

    // Un segundo mensaje por el mismo archivo no lo vuelve a purgar: el procesador lo salta antes.
    [Fact]
    public void PurgingALetGoProofRequiresAnAvailableResource()
    {
        var proof = AvailableProof("comprobante.pdf", "application/pdf");
        proof.PurgeDetachedPaymentProof(Now);

        var error = Assert.Throws<StorageDomainException>(() => proof.PurgeDetachedPaymentProof(Now.AddHours(1)));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Equal(Now, proof.DeletedAt);
    }

    // D13: el archivo User de un comprobante v1 nunca se purga por esto.
    [Fact]
    public void PurgingALetGoProofRequiresAPaymentProof()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.User,
            "comprobante.pdf", "application/pdf", 4096, "staging/tenants/a/c", Now);
        file.CompleteUpload("checksum", 4096, Now);
        file.MarkClean(Now);

        var error = Assert.Throws<StorageDomainException>(() => file.PurgeDetachedPaymentProof(Now));

        Assert.Equal("storage.file.invalid_state", error.Code);
        Assert.Equal(FileResourceStatus.Available, file.Status);
    }

    private static FileResource PendingScanProof(string name, string mimeType)
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), Guid.CreateVersion7(), Guid.CreateVersion7(), FileOwnerType.PaymentProof,
            name, mimeType, 4096, "staging/tenants/a/b", Now);
        proof.CompleteUpload("original-checksum", 4096, Now);
        return proof;
    }

    private static FileResource AvailableProof(string name, string mimeType)
    {
        var proof = PendingScanProof(name, mimeType);
        proof.MarkClean(Now);
        return proof;
    }
}
