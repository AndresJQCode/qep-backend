using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Purchases.Domain;
using Modules.Purchases.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using static Modules.Purchases.IntegrationTests.PurchasesApiHarness;

namespace Modules.Purchases.IntegrationTests;

/// <summary>
/// La sonda de retención de Compras: Identity no borra a un usuario huérfano mientras alguna de sus membresías haya creado una
/// orden o dejado un paso en su historial. Host propio: la sonda lee toda la base, sin tenant.
/// </summary>
public sealed class PurchasesUserReferenceProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCreatorOfAPurchaseOrderIsRetained()
    {
        await using var database = await StartDatabaseAsync();
        await using var factory = new QepApiFactory(database.GetConnectionString());
        var creator = await NewTenantAsync(factory);
        var bystander = await NewTenantAsync(factory);
        Assert.False(await RetainsAsync(factory, creator.SubjectId));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var purchases = scope.ServiceProvider.GetRequiredService<PurchasesDbContext>();
            purchases.PurchaseOrders.Add(PurchaseOrder.Create(
                creator.TenantId,
                "OC-0001",
                Guid.CreateVersion7(),
                null,
                "COP",
                null,
                [new PurchaseOrderLineDraft(Guid.CreateVersion7(), "SKU-1", "Camiseta", 2m, 10m, 19m)],
                creator.MemberId,
                DateTimeOffset.UtcNow));
            await purchases.SaveChangesAsync(Ct);
        }

        Assert.True(await RetainsAsync(factory, creator.SubjectId));
        Assert.False(await RetainsAsync(factory, bystander.SubjectId));
    }

    [Fact]
    public async Task WhoLeftAHistoryStepIsRetained()
    {
        await using var database = await StartDatabaseAsync();
        await using var factory = new QepApiFactory(database.GetConnectionString());
        var creator = await NewTenantAsync(factory);
        var sender = await NewTenantAsync(factory);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var purchases = scope.ServiceProvider.GetRequiredService<PurchasesDbContext>();
            var order = PurchaseOrder.Create(
                creator.TenantId,
                "OC-0001",
                Guid.CreateVersion7(),
                null,
                "COP",
                null,
                [new PurchaseOrderLineDraft(Guid.CreateVersion7(), "SKU-1", "Camiseta", 2m, 10m, 19m)],
                creator.MemberId,
                DateTimeOffset.UtcNow);
            // Sólo para la sonda: en la API el historial siempre nombra una membresía del mismo tenant.
            order.Send(sender.MemberId, DateTimeOffset.UtcNow);
            purchases.PurchaseOrders.Add(order);
            await purchases.SaveChangesAsync(Ct);
        }

        Assert.True(await RetainsAsync(factory, sender.SubjectId));
    }

    private static async Task<bool> RetainsAsync(QepApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var probe = scope.ServiceProvider.GetServices<IUserReferenceProbe>().Single(candidate => candidate.Source == "purchases");
        return await probe.HasReferencesAsync(userId, Ct);
    }
}
