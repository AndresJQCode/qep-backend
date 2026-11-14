using BuildingBlocks.Application;
using Modules.Shopify.Application;
using Modules.Shopify.Domain;
using static Modules.Shopify.UnitTests.StoreHandlersWorld;

namespace Modules.Shopify.UnitTests;

/// <summary>
/// D7: borrar es idempotente. Dos <c>DELETE</c> simultáneos del mismo id: el segundo encuentra la fila, pero su guardado no afecta
/// ninguna fila y <c>ShopifyUnitOfWork</c> lo traduce a <c>concurrency.conflict</c>. Eso es «ya borrada»: 204 y sin auditar.
/// </summary>
public sealed class DeleteShopifyCustomLineMappingHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (DeleteShopifyCustomLineMappingHandler Handler, StoreHandlersWorld World, ShopifyCustomLineMapping Mapping) Arrange()
    {
        var world = new StoreHandlersWorld();
        var mapping = ShopifyCustomLineMapping.Create(TenantId, ConnectionId, "Garantía", Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        var handler = new DeleteShopifyCustomLineMappingHandler(
            world.Context, new ShopifyEnabledModules(), world.Connections, new SingleMappingRepository(mapping), world.UnitOfWork,
            new FakeAuditRecorder(world.Database), world.Clock);
        return (handler, world, mapping);
    }

    [Fact]
    public async Task ALostDeleteRaceIsTreatedAsAlreadyDeleted()
    {
        var (handler, world, mapping) = Arrange();
        world.UnitOfWork.BeforeSave = _ => throw new RequestConcurrencyException("concurrency.conflict", "The row was deleted by someone else.");

        var deleted = await handler.HandleAsync(new DeleteShopifyCustomLineMappingCommand(TenantId, ConnectionId, mapping.Id), Ct);

        Assert.False(deleted);
        Assert.Empty(world.Database.Audit);
    }

    [Fact]
    public async Task AnotherConcurrencyCodeStillPropagates()
    {
        var (handler, world, mapping) = Arrange();
        world.UnitOfWork.BeforeSave = _ => throw new RequestConcurrencyException("something.else", "Other.");

        await Assert.ThrowsAsync<RequestConcurrencyException>(
            () => handler.HandleAsync(new DeleteShopifyCustomLineMappingCommand(TenantId, ConnectionId, mapping.Id), Ct));
    }

    private sealed class SingleMappingRepository(ShopifyCustomLineMapping mapping) : IShopifyCustomLineMappingRepository
    {
        public Task<ShopifyCustomLineMapping?> FindAsync(Guid tenantId, Guid connectionId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<ShopifyCustomLineMapping?>(id == mapping.Id ? mapping : null);

        public void Remove(ShopifyCustomLineMapping toRemove)
        {
        }

        public Task<IReadOnlyDictionary<string, Guid>> FindVariantIdsByTitlesAsync(
            Guid tenantId, Guid connectionId, IReadOnlyCollection<string> titles, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ShopifyCustomLineMapping>> ListAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public void Add(ShopifyCustomLineMapping toAdd) => throw new NotSupportedException();
    }
}
