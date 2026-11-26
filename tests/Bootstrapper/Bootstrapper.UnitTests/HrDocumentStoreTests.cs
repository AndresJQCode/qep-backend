using Modules.Hr.Application;
using Modules.Hr.Domain;
using Modules.Storage.Application;
using Modules.Storage.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-20: el PDF de un contrato o de un registro, sobre Storage. Es de dueño de sistema
/// <see cref="FileOwnerType.HrDocument"/> con el tenant como dueño: sube por <c>IStorageSystemUploads</c> y no aparece en la consola
/// de Storage. Toda lectura filtra por tenant y dueño: un id ajeno es el mismo «no existe».
/// </summary>
public sealed class HrDocumentStoreTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeStorageSystemUploads _uploads = new();

    private HrDocumentStore NewStore() => new(_uploads);

    [Fact]
    public async Task OpenCreatesAnHrDocumentOwnedByTheTenantWithTheTenMegabyteCap()
    {
        var session = await NewStore().OpenUploadAsync(TenantId, "contrato.pdf", "application/pdf", 1024, Ct);

        var opened = Assert.Single(_uploads.Opens);
        Assert.Equal((TenantId, FileOwnerType.HrDocument, TenantId), (opened.TenantId, opened.OwnerType, opened.OwnerId));
        Assert.Equal(
            ("contrato.pdf", "application/pdf", 1024L, HrDocumentPolicy.MaxSizeBytes),
            (opened.Name, opened.MimeType, opened.SizeBytes, opened.MaxSizeBytes));
        Assert.Equal(10_485_760L, HrDocumentPolicy.MaxSizeBytes);
        Assert.Equal(_uploads.Session.FileId, session.FileId);
        Assert.Equal(_uploads.Session.UploadUrl, session.UploadUrl);
        Assert.Equal(_uploads.Session.Headers, session.Headers);
    }

    // El caso de uso ya filtró nombre, tipo y tamaño; si Storage igual rechaza la declaración, la pantalla ve el código de Hr.
    [Theory]
    [InlineData("contrato.png", "image/png", 1024)]
    [InlineData("contrato.pdf", "application/pdf", 10_485_761)]
    public async Task AStorageDeclarationRejectionIsFileInvalid(string fileName, string mimeType, long sizeBytes)
    {
        var error = await Assert.ThrowsAsync<HrDomainException>(
            () => NewStore().OpenUploadAsync(TenantId, fileName, mimeType, sizeBytes, Ct));

        Assert.Equal("hr.file.invalid", error.Code);
        Assert.Empty(_uploads.Opens);
    }

    [Fact]
    public async Task CompleteReturnsTheStoredFileName()
    {
        var fileId = _uploads.Seed(
            TenantId, FileOwnerType.HrDocument, TenantId, SystemUploadOutcome.Available, "application/pdf", name: "contrato-firmado.pdf");

        var file = await NewStore().CompleteAsync(TenantId, fileId, Ct);

        Assert.Equal(new HrDocumentFile(HrDocumentState.Available, "contrato-firmado.pdf"), file);
    }

    [Theory]
    [InlineData(SystemUploadOutcome.ObjectMissing, HrDocumentState.NotUploaded)]
    [InlineData(SystemUploadOutcome.Rejected, HrDocumentState.Rejected)]
    [InlineData(SystemUploadOutcome.NotFound, HrDocumentState.NotFound)]
    public async Task CompleteMapsTheOutcome(SystemUploadOutcome outcome, HrDocumentState expected)
    {
        var fileId = _uploads.Seed(TenantId, FileOwnerType.HrDocument, TenantId, outcome, "application/pdf");

        var file = await NewStore().CompleteAsync(TenantId, fileId, Ct);

        Assert.Equal(new HrDocumentFile(expected, null), file);
    }

    [Fact]
    public async Task AFileOfAnotherTenantIsNotFound()
    {
        var otherTenant = Guid.CreateVersion7();
        var fileId = _uploads.Seed(otherTenant, FileOwnerType.HrDocument, otherTenant, SystemUploadOutcome.Available, "application/pdf");

        var file = await NewStore().CompleteAsync(TenantId, fileId, Ct);

        Assert.Equal(HrDocumentState.NotFound, file.State);
    }

    [Fact]
    public async Task TheDownloadUrlIsNullForAnotherOwnersFile()
    {
        var support = _uploads.Seed(TenantId, FileOwnerType.SupportTicket, Guid.CreateVersion7(), SystemUploadOutcome.Available, "application/pdf");
        var unknown = Guid.CreateVersion7();

        Assert.Null(await NewStore().CreateDownloadUrlAsync(TenantId, support, Ct));
        Assert.Null(await NewStore().CreateDownloadUrlAsync(TenantId, unknown, Ct));
        Assert.Empty(_uploads.Downloads);
    }

    [Fact]
    public async Task TheDownloadUrlOfAnAvailableHrDocumentIsSigned()
    {
        var fileId = _uploads.Seed(TenantId, FileOwnerType.HrDocument, TenantId, SystemUploadOutcome.Available, "application/pdf");

        var url = await NewStore().CreateDownloadUrlAsync(TenantId, fileId, Ct);

        Assert.NotNull(url);
        Assert.Equal([fileId], _uploads.Downloads);
    }
}
