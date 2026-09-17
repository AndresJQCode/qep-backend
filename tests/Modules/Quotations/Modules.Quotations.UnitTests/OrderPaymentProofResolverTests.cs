using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Los tipos de comprobante que acepta un pedido. Desde v2 (spec 2026-09-16) toda imagen de un
/// comprobante llega procesada a WebP (D7), así que WebP se suma a los tres de US-14. Desde la revisión
/// final (I2), un PaymentProof que otro comprobante ya usa no está disponible.
/// </summary>
public sealed class OrderPaymentProofResolverTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public async Task AcceptsTheProofTypes(string mimeType)
    {
        var fileId = Guid.CreateVersion7();
        var lookup = new SingleFileLookup(new QuotationFileRef(fileId, TenantId, mimeType, 1024, IsAvailable: true));

        var exception = await Record.ExceptionAsync(() => OrderPaymentProofResolver.ResolveAsync(
            lookup, new StubOrderListRepository(), TenantId, fileId, exceptProofId: null,
            TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RejectsAnotherImageType()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = new SingleFileLookup(new QuotationFileRef(fileId, TenantId, "image/gif", 1024, IsAvailable: true));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => OrderPaymentProofResolver.ResolveAsync(
            lookup, new StubOrderListRepository(), TenantId, fileId, exceptProofId: null,
            TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_type_not_allowed", error.Code);
    }

    // Revisión final (I2): un PaymentProof ya adjunto no se adjunta otra vez, y la pregunta excluye el
    // comprobante que se reemplaza.
    [Fact]
    public async Task RejectsAPaymentProofAlreadyInUse()
    {
        var fileId = Guid.CreateVersion7();
        var exceptProofId = new OrderPaymentProofId(Guid.CreateVersion7());
        var lookup = new SingleFileLookup(new QuotationFileRef(
            fileId, TenantId, "image/webp", 1024, IsAvailable: true, IsPaymentProof: true));
        var repository = new StubOrderListRepository { PaymentProofFileInUse = true };

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => OrderPaymentProofResolver.ResolveAsync(
            lookup, repository, TenantId, fileId, exceptProofId, TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_available", error.Code);
        Assert.Equal((fileId, (OrderPaymentProofId?)exceptProofId), Assert.Single(repository.PaymentProofFileQuestions));
    }

    // D13: un archivo User se sigue adjuntando como en v1, sin preguntar si ya se usa.
    [Fact]
    public async Task AcceptsAUserFileAlreadyInUse()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = new SingleFileLookup(new QuotationFileRef(fileId, TenantId, "application/pdf", 1024, IsAvailable: true));
        var repository = new StubOrderListRepository { PaymentProofFileInUse = true };

        var exception = await Record.ExceptionAsync(() => OrderPaymentProofResolver.ResolveAsync(
            lookup, repository, TenantId, fileId, exceptProofId: null, TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.Empty(repository.PaymentProofFileQuestions);
    }

    private sealed class SingleFileLookup(QuotationFileRef file) : IQuotationFileLookup
    {
        public Task<QuotationFileRef?> FindAsync(
            Guid tenantId, Guid fileId, CancellationToken cancellationToken) =>
            Task.FromResult<QuotationFileRef?>(file.FileId == fileId ? file : null);

        public Task<string> CreateDownloadUrlAsync(
            Guid tenantId, Guid fileId, string downloadFileName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
