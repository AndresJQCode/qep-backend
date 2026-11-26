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
        var lookup = new CountingFileLookup(new QuotationFileRef(fileId, TenantId, mimeType, 1024, IsAvailable: true));

        var exception = await Record.ExceptionAsync(() => OrderPaymentProofResolver.ResolveAsync(
            lookup, new StubOrderListRepository(), TenantId, fileId, exceptProofId: null,
            TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RejectsAnotherImageType()
    {
        var fileId = Guid.CreateVersion7();
        var lookup = new CountingFileLookup(new QuotationFileRef(fileId, TenantId, "image/gif", 1024, IsAvailable: true));

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
        var lookup = new CountingFileLookup(new QuotationFileRef(
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
        var lookup = new CountingFileLookup(new QuotationFileRef(fileId, TenantId, "application/pdf", 1024, IsAvailable: true));
        var repository = new StubOrderListRepository { PaymentProofFileInUse = true };

        var exception = await Record.ExceptionAsync(() => OrderPaymentProofResolver.ResolveAsync(
            lookup, repository, TenantId, fileId, exceptProofId: null, TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.Empty(repository.PaymentProofFileQuestions);
    }

    // Consultas por request, fase 2: con la base en otro nodo, cada lectura es un viaje. Tres comprobantes son una
    // lectura de archivos y una de uso, no tres de cada una.
    [Fact]
    public async Task ThreeProofsAreTwoReadsNotSix()
    {
        var files = new[] { Pdf(), Pdf(), Pdf() };
        var lookup = new CountingFileLookup(files);
        var orders = new StubOrderListRepository();

        await OrderPaymentProofResolver.ResolveManyAsync(
            lookup, orders, TenantId, files.Select(file => file.FileId).ToArray(), null, TestContext.Current.CancellationToken);

        Assert.Equal(1, lookup.FindManyCalls);
        Assert.Equal(1, orders.PaymentProofFileBatches);
        Assert.Equal(0, lookup.FindCalls);
    }

    [Fact]
    public async Task AFileOfAnotherTenantIsNotFound()
    {
        var foreign = Pdf(tenantId: Guid.CreateVersion7());

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => OrderPaymentProofResolver.ResolveManyAsync(
            new CountingFileLookup(foreign), new StubOrderListRepository(), TenantId, [foreign.FileId], null,
            TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_found", error.Code);
    }

    // Review Focus 4: el mismo comprobante dos veces en el cuerpo es «ya adjunto», aunque la base todavía no lo tenga.
    [Fact]
    public async Task TheSameFileTwiceInOneBodyIsNotAvailable()
    {
        var proof = Pdf(isPaymentProof: true);

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => OrderPaymentProofResolver.ResolveManyAsync(
            new CountingFileLookup(proof), new StubOrderListRepository(), TenantId, [proof.FileId, proof.FileId], null,
            TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_available", error.Code);
    }

    // El error es el del primer archivo que falla en el orden del cuerpo, como cuando se resolvían de a uno.
    [Fact]
    public async Task TheErrorIsTheFirstFailingFileInBodyOrder()
    {
        var tooLarge = Pdf() with { SizeBytes = 11 * 1024 * 1024 };
        var gif = Pdf() with { MimeType = "image/gif" };

        var error = await Assert.ThrowsAsync<QuotationsDomainException>(() => OrderPaymentProofResolver.ResolveManyAsync(
            new CountingFileLookup(tooLarge, gif), new StubOrderListRepository(), TenantId, [Pdf().FileId, tooLarge.FileId, gif.FileId], null,
            TestContext.Current.CancellationToken));

        Assert.Equal("order.payment_proof.file_not_found", error.Code);
    }

    private static QuotationFileRef Pdf(Guid? tenantId = null, bool isPaymentProof = true) =>
        new(Guid.CreateVersion7(), tenantId ?? TenantId, "application/pdf", 1024, IsAvailable: true, isPaymentProof);
}

/// <summary>Los archivos que siembra la prueba. Cuenta las lecturas de a uno y las de lote, para que una prueba afirme
/// cuántos viajes a Storage costó un cuerpo.</summary>
internal sealed class CountingFileLookup(params QuotationFileRef[] files) : IQuotationFileLookup
{
    public int FindCalls { get; private set; }

    public int FindManyCalls { get; private set; }

    public Task<QuotationFileRef?> FindAsync(
        Guid tenantId, Guid fileId, CancellationToken cancellationToken)
    {
        FindCalls++;
        return Task.FromResult(files.FirstOrDefault(file => file.FileId == fileId));
    }

    // Sin filtro de tenant a propósito: la regla de tenant del resolvedor se prueba aunque el adaptador ya la aplique.
    public Task<IReadOnlyDictionary<Guid, QuotationFileRef>> FindManyAsync(
        Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        FindManyCalls++;
        return Task.FromResult<IReadOnlyDictionary<Guid, QuotationFileRef>>(
            files.Where(file => fileIds.Contains(file.FileId)).ToDictionary(file => file.FileId));
    }

    public Task<string> CreateDownloadUrlAsync(
        Guid tenantId, Guid fileId, string downloadFileName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
