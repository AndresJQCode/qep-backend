using BuildingBlocks.Application;
using Modules.Siigo.Application;
using Modules.Siigo.Application.Products;
using Modules.Siigo.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;

namespace Modules.Siigo.UnitTests;

/// <summary>La tabla de vínculos y el selector de productos con una página enorme: el desplazamiento no se desborda (en la tabla saldría
/// negativo; en el selector volvería la primera página). La página se recorta y la respuesta devuelve la que se usó.</summary>
public sealed class SiigoProductPagingTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    [Theory]
    [InlineData(int.MaxValue)]
    // La primera cuyo desplazamiento de a 20 ya no cabe en un int.
    [InlineData((int.MaxValue / 20) + 2)]
    public void AHugePageOfThePickerIsClampedAndComesBackEmpty(int page)
    {
        IReadOnlyList<SiigoProductOption> products = [new("id-1", "A-1", "Producto", true)];

        var result = SiigoProductPicker.Page(products, null, page);

        Assert.Empty(result.Items);
        Assert.Equal((int.MaxValue / SiigoProductPicker.PageSize, 1), (result.Page, result.Total));
    }

    [Theory]
    [InlineData(null, 1, 0)]
    [InlineData(3, 3, 100)]
    [InlineData(int.MaxValue / 50, int.MaxValue / 50, ((int.MaxValue / 50) - 1) * 50)]
    [InlineData(int.MaxValue, int.MaxValue / 50, ((int.MaxValue / 50) - 1) * 50)]
    public async Task TheLinkTableNeverAsksTheCatalogForAnOverflowedOffset(int? requested, int used, int skip)
    {
        var catalog = new RecordingCatalog();
        var handler = new ListSiigoVariantLinksHandler(
            new ExecutionContext(),
            new AllModules(),
            new KnownConnection(),
            new EmptySettings(),
            catalog,
            new ListSiigoVariantLinksValidator());

        var result = await handler.HandleAsync(
            new ListSiigoVariantLinksQuery(TenantId, Guid.CreateVersion7(), null, null, requested), TestContext.Current.CancellationToken);

        Assert.Equal(skip, catalog.Skip);
        Assert.Equal(used, result.Page);
    }

    private sealed class RecordingCatalog : ISiigoVariantCatalog
    {
        public int? Skip { get; private set; }

        public Task<SiigoPage<SiigoVariantRef>> SearchAsync(
            Guid tenantId, string? search, IReadOnlyCollection<Guid>? onlyVariantIds, IReadOnlyCollection<Guid>? excludeVariantIds, int skip,
            int take, CancellationToken cancellationToken)
        {
            Skip = skip;
            return Task.FromResult(new SiigoPage<SiigoVariantRef>([], 1, take, 0));
        }

        public Task<int> CountActiveAsync(Guid tenantId, IReadOnlyCollection<Guid>? onlyVariantIds, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<SiigoVariantRef>> ListAllActiveCodesAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class EmptySettings : ISiigoSettingsRepository
    {
        public Task<SiigoSettings?> FindOrCreateAsync(
            Guid tenantId, Guid connectionId, bool withVariantLinks, CancellationToken cancellationToken) =>
            Task.FromResult<SiigoSettings?>(SiigoSettings.Create(connectionId, tenantId));

        public Task<SiigoSettings?> FindAsync(
            Guid tenantId, Guid connectionId, bool withVariantLinks, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SiigoSettings?> FindByCompanyAsync(
            Guid tenantId, Guid companyId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Remove(SiigoSettings settings) => throw new NotSupportedException();
    }

    private sealed class KnownConnection : ISiigoConnectionDirectory
    {
        public Task<SiigoConnectionInfo?> FindAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            Task.FromResult<SiigoConnectionInfo?>(new SiigoConnectionInfo(connectionId, "Siigo", "Active"));

        public Task<SiigoConnectionResolution> ResolveAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SiigoConnectionResolution> ResolveForSetupAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SiigoConnectionInfo>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ExecutionContext : IExecutionContext
    {
        public Guid SubjectId { get; } = Guid.CreateVersion7();

        public TenantId TenantId { get; } = new(SiigoProductPagingTests.TenantId);

        public bool HasPermission(string permission) => permission == SiigoPermissions.SettingsManage;
    }

    private sealed class AllModules : ITenantModules
    {
        public Task<TenantModuleSet?> FindAsync(Guid tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantModuleSet?>(null);
    }
}
