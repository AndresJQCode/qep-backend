using Modules.Quotations.Application;
using Modules.Quotations.Domain;

namespace Modules.Quotations.UnitTests;

/// <summary>
/// Los tipos de comprobante que acepta un pedido. Desde v2 (spec 2026-09-16) toda imagen de un
/// comprobante llega procesada a WebP (D7), así que WebP se suma a los tres de US-14.
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

        var exception = await Record.ExceptionAsync(() =>
            OrderPaymentProofResolver.ResolveAsync(lookup, TenantId, fileId, TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RejectsAnotherImageType()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = new SingleFileLookup(new QuotationFileRef(fileId, TenantId, "image/gif", 1024, IsAvailable: true));

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() =>
            OrderPaymentProofResolver.ResolveAsync(lookup, TenantId, fileId, TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_type_not_allowed", error.Code);
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
