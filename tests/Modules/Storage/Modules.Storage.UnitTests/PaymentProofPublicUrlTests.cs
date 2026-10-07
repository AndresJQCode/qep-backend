using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Storage.UnitTests;

/// <summary>
/// La URL pública que devuelve el listado de archivos para un comprobante (revisión final, M6): la de
/// uno movido sí, la de uno purgado no. La purga de D19 conserva <c>PublicStorageKey</c>, pero el objeto
/// ya se borró y la URL no abre.
/// </summary>
public sealed class PaymentProofPublicUrlTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AMovedProofListsItsPublicUrl()
    {
        var proof = MovedPaymentProof();

        var file = Assert.Single((await ListAsync(proof)).Items);

        Assert.Equal($"{FixedPublicObjectStorage.BaseUrl}/payment-proofs/abc.pdf", file.PublicUrl);
    }

    [Fact]
    public async Task APurgedProofListsNoPublicUrl()
    {
        var proof = MovedPaymentProof();
        proof.PurgeDetachedPaymentProof(Now);

        var file = Assert.Single((await ListAsync(proof)).Items);

        Assert.Equal("Purged", file.Status);
        Assert.Null(file.PublicUrl);
    }

    [Fact]
    public async Task WithoutOrdersTheHandlerExcludesPaymentProofs()
    {
        var repository = new InMemoryFileResourceRepository(MovedPaymentProof());

        await ListAsync(repository, FixedTenantModules.AllBut(TenantModuleKeys.Orders));

        Assert.Equal([FileOwnerType.PaymentProof], repository.LastExcludedOwnerTypes);
    }

    [Fact]
    public async Task WithEverythingOnNothingIsExcluded()
    {
        var repository = new InMemoryFileResourceRepository(MovedPaymentProof());

        await ListAsync(repository, FixedTenantModules.AllBut());

        Assert.Empty(repository.LastExcludedOwnerTypes!);
    }

    [Fact]
    public async Task ASimulatedTenantExcludesNothing()
    {
        var repository = new InMemoryFileResourceRepository(MovedPaymentProof());

        await ListAsync(repository, FixedTenantModules.Simulated);

        Assert.Empty(repository.LastExcludedOwnerTypes!);
    }

    private static Task<PagedFilesDto> ListAsync(FileResource resource) =>
        ListAsync(new InMemoryFileResourceRepository(resource), FixedTenantModules.Simulated);

    private static Task<PagedFilesDto> ListAsync(InMemoryFileResourceRepository repository, ITenantModules modules) =>
        new ListFilesHandler(
                repository,
                modules,
                new FixedPublicObjectStorage(),
                new AllowAllExecutionContext(TenantId))
            .HandleAsync(
                new ListFilesQuery(TenantId, null, null, null, null, null, null, 1, 20),
                TestContext.Current.CancellationToken);

    private static FileResource MovedPaymentProof()
    {
        var proof = FileResource.CreatePendingUpload(
            FileResourceId.New(), TenantId, Guid.CreateVersion7(), FileOwnerType.PaymentProof,
            "comprobante.pdf", "application/pdf", 2048, $"staging/tenants/{TenantId:N}/comprobante", Now);
        proof.CompleteUpload("checksum", 2048, Now);
        proof.MarkClean(Now);
        proof.MoveToPublic("payment-proofs/abc.pdf", Now);
        return proof;
    }
}
