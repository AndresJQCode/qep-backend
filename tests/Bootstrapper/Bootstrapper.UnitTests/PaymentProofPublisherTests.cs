using System.Globalization;
using Modules.Quotations.Domain;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Los dos publicadores de comprobantes de pago (spec 2026-09-15, P3 a P6): el que copia al bucket
/// público con una clave aleatoria y el que no hace nada con la opción apagada.
/// </summary>
public sealed class PaymentProofPublisherTests
{
    private const string PdfKeyPattern = "^payment-proofs/[0-9a-f]{32}\\.pdf$";

    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    // P5: la copia va bajo payment-proofs/ con una clave aleatoria —no el id del archivo, que viaja
    // en el navegador— y sale de la clave privada del archivo.
    [Fact]
    public async Task CopiesThePrivateObjectUnderPaymentProofsWithARandomKey()
    {
        var file = AvailableFile(TenantId, "application/pdf", "comprobante.pdf");
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(file), storage);

        var key = await publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(key);
        Assert.Matches(PdfKeyPattern, key);
        Assert.DoesNotContain(
            file.Id.Value.ToString("N", CultureInfo.InvariantCulture), key, StringComparison.Ordinal);
        var copy = Assert.Single(storage.Copies);
        Assert.Equal(key, copy.Key);
        Assert.Equal(file.StorageKey, copy.Value);
    }

    // Clave nueva en cada publicación, igual que el PDF de la cotización: dos comprobantes con el
    // mismo archivo no comparten copia.
    [Fact]
    public async Task EachPublicationGetsItsOwnKey()
    {
        var file = AvailableFile(TenantId, "application/pdf", "comprobante.pdf");
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(file), storage);

        var first = await publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);
        var second = await publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.Equal(2, storage.Copies.Count);
    }

    // P5: la extensión sale del MimeType, no del nombre, que puede traer ".jpeg", mayúsculas o nada.
    [Theory]
    [InlineData("application/pdf", "comprobante.PDF", ".pdf")]
    [InlineData("image/jpeg", "foto.jpeg", ".jpg")]
    [InlineData("IMAGE/PNG", "captura", ".png")]
    public async Task TheExtensionComesFromTheMimeType(string mimeType, string name, string extension)
    {
        var file = AvailableFile(TenantId, mimeType, name);
        var publisher = new PublicPaymentProofPublisher(
            new InMemoryFileResourceRepository(file), new RecordingPublicObjectStorage());

        var key = await publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken);

        Assert.NotNull(key);
        Assert.StartsWith("payment-proofs/", key, StringComparison.Ordinal);
        Assert.EndsWith(extension, key, StringComparison.Ordinal);
    }

    // El resolver ya revisó la frontera de tenant, pero se revisa igual: mismo código para "no
    // existe" y "es de otro tenant", y sin copia.
    [Fact]
    public async Task AFileOfAnotherTenantIsRejectedAsNotFoundWithoutCopying()
    {
        var file = AvailableFile(Guid.CreateVersion7(), "application/pdf", "comprobante.pdf");
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(file), storage);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_found", error.Code);
        Assert.Empty(storage.Copies);
    }

    [Fact]
    public async Task AMissingFileIsRejectedAsNotFound()
    {
        var publisher = new PublicPaymentProofPublisher(
            new InMemoryFileResourceRepository(), new RecordingPublicObjectStorage());

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            publisher.PublishAsync(TenantId, Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_found", error.Code);
    }

    [Fact]
    public async Task AFileThatHasNotFinishedUploadingIsRejected()
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.User, "comprobante.pdf",
            "application/pdf", 1024, $"staging/{Guid.CreateVersion7():N}", Now);
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(file), storage);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            publisher.PublishAsync(TenantId, file.Id.Value, TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_available", error.Code);
        Assert.Empty(storage.Copies);
    }

    // P7: el rollback borra la copia del bucket público.
    [Fact]
    public async Task DeleteRemovesThePublicCopy()
    {
        var storage = new RecordingPublicObjectStorage();
        var publisher = new PublicPaymentProofPublisher(new InMemoryFileResourceRepository(), storage);

        await publisher.DeleteAsync("payment-proofs/abc.pdf", TestContext.Current.CancellationToken);

        Assert.Equal(["payment-proofs/abc.pdf"], storage.DeletedKeys);
    }

    // P5: la URL se arma al exportar con el dominio público; no se guarda.
    [Fact]
    public void UrlForBuildsThePublicUrlOfTheKey()
    {
        var publisher = new PublicPaymentProofPublisher(
            new InMemoryFileResourceRepository(), new RecordingPublicObjectStorage());

        Assert.Equal(
            $"{RecordingPublicObjectStorage.BaseUrl}/payment-proofs/abc.pdf",
            publisher.UrlFor("payment-proofs/abc.pdf"));
    }

    // P1: apagada, no se copia nada y el Excel no tiene URL, aunque el comprobante tenga una copia
    // de cuando la opción estaba encendida.
    [Fact]
    public async Task TheDisabledPublisherCopiesNothingAndGivesNoUrl()
    {
        var publisher = new DisabledPaymentProofPublisher();

        var key = await publisher.PublishAsync(
            TenantId, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        await publisher.DeleteAsync("payment-proofs/abc.pdf", TestContext.Current.CancellationToken);

        Assert.Null(key);
        Assert.Null(publisher.UrlFor("payment-proofs/abc.pdf"));
    }

    private static FileResource AvailableFile(Guid tenantId, string mimeType, string name)
    {
        var file = FileResource.CreatePendingUpload(
            FileResourceId.New(), tenantId, Guid.CreateVersion7(), FileOwnerType.User, name, mimeType, 1024,
            $"staging/{Guid.CreateVersion7():N}", Now);
        file.CompleteUpload("checksum", 1024, Now);
        file.Promote($"files/tenants/{tenantId:N}/{Guid.CreateVersion7():N}", Now);
        return file;
    }
}
