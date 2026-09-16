using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Infrastructure.PaymentProofs;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// La reconciliación de payment-proofs/ (spec 2026-09-16, D12 y sección 4). Con DryRun en false
/// borra el huérfano viejo y respeta el referenciado —por un archivo movido o por un pedido— y el
/// reciente, sin listar otros prefijos. Con DryRun en true no borra nada.
/// </summary>
public sealed class PaymentProofOrphanCleanupTests
{
    [Fact]
    public async Task ARealRunDeletesOnlyOldUnreferencedPaymentProofs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString(), orphanCleanupDryRun: false);
        using var client = CreateClient(factory);
        var old = DateTimeOffset.UtcNow.AddHours(-48);
        var orphan = NewPublicKey(".pdf");
        var recent = NewPublicKey(".pdf");
        var referencedByAnOrder = NewPublicKey(".webp");
        var moved = await MovedProofKeyAsync(client, factory);
        factory.PublicObjectStorage.Put(orphan, old);
        factory.PublicObjectStorage.Put(recent, DateTimeOffset.UtcNow.AddHours(-1));
        factory.PublicObjectStorage.Put(referencedByAnOrder, old);
        factory.PublicObjectStorage.Put(moved, old);
        factory.PublicObjectStorage.Put("tenants/abc/media/def/original.png", old);
        factory.PublicObjectStorage.Put("quotations/tenants/abc/cotizacion.pdf", old);
        factory.PublicReferences.Reference(referencedByAnOrder);

        var result = await RunOrphanCleanupAsync(factory);

        Assert.Equal(
            new PaymentProofOrphanCleanupResult(Listed: 4, Recent: 1, Referenced: 2, Orphans: 1, Deleted: 1),
            result);
        Assert.Equal([orphan], factory.PublicObjectStorage.DeletedKeys);
        Assert.False(factory.PublicObjectStorage.Exists(orphan));
        Assert.True(factory.PublicObjectStorage.Exists(recent));
        Assert.True(factory.PublicObjectStorage.Exists(referencedByAnOrder));
        Assert.True(factory.PublicObjectStorage.Exists(moved));
        Assert.True(factory.PublicObjectStorage.Exists("tenants/abc/media/def/original.png"));
        Assert.True(factory.PublicObjectStorage.Exists("quotations/tenants/abc/cotizacion.pdf"));
        // Cuatro objetos de a dos por página: dos páginas, las dos de payment-proofs/.
        Assert.Equal(["payment-proofs/", "payment-proofs/"], factory.PublicObjectStorage.ListedPrefixes);
        var audit = Assert.Single(
            await AuditEventsAsync(factory), entry => entry.Action == "storage.public_object.purged");
        Assert.Equal(orphan, audit.ResourceId);
        Assert.Equal("public_object", audit.ResourceType);
        Assert.Equal("System", audit.ActorType);
        Assert.Null(audit.TenantId);
    }

    // D12: arranca en modo solo-registrar; nada se borra hasta apagar DryRun a mano.
    [Fact]
    public async Task ADryRunDeletesNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var orphan = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(orphan, DateTimeOffset.UtcNow.AddHours(-48));

        var result = await RunOrphanCleanupAsync(factory);

        Assert.Equal(
            new PaymentProofOrphanCleanupResult(Listed: 1, Recent: 0, Referenced: 0, Orphans: 1, Deleted: 0),
            result);
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.True(factory.PublicObjectStorage.Exists(orphan));
        Assert.DoesNotContain(
            await AuditEventsAsync(factory), entry => entry.Action == "storage.public_object.purged");
    }

    // Mismo criterio que el barrido de staging (Task 9) y el retiro (Task 9C): sin ninguna sonda no se
    // puede saber si un pedido todavía enlaza el objeto, así que no se borra nada, aunque DryRun esté
    // apagado.
    [Fact]
    public async Task WithoutProbesNothingIsDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString(), orphanCleanupDryRun: false);
        var orphan = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(orphan, DateTimeOffset.UtcNow.AddHours(-48));

        PaymentProofOrphanCleanupResult result;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var processor = ActivatorUtilities.CreateInstance<PaymentProofOrphanCleanupProcessor>(
                scope.ServiceProvider, new List<IPublicObjectReferenceProbe>());
            result = await processor.CleanupAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(new PaymentProofOrphanCleanupResult(0, 0, 0, 0, 0), result);
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.True(factory.PublicObjectStorage.Exists(orphan));
        Assert.DoesNotContain(
            await AuditEventsAsync(factory), entry => entry.Action == "storage.public_object.purged");
    }

    // Un borrado que falla (R2 caído un momento) es transitorio: no frena a los demás huérfanos, no se
    // audita y el objeto vuelve a aparecer en la corrida siguiente.
    [Fact]
    public async Task AFailedDeleteDoesNotStopTheRunAndIsRetriedNextTime()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString(), orphanCleanupDryRun: false);
        var old = DateTimeOffset.UtcNow.AddHours(-48);
        var failing = NewPublicKey(".pdf");
        var other = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(failing, old);
        factory.PublicObjectStorage.Put(other, old);
        factory.PublicObjectStorage.FailingDeleteKey = failing;

        var result = await RunOrphanCleanupAsync(factory);

        Assert.Equal(
            new PaymentProofOrphanCleanupResult(Listed: 2, Recent: 0, Referenced: 0, Orphans: 2, Deleted: 1),
            result);
        Assert.True(factory.PublicObjectStorage.Exists(failing));
        Assert.False(factory.PublicObjectStorage.Exists(other));
        var purged = Assert.Single(
            await AuditEventsAsync(factory), entry => entry.Action == "storage.public_object.purged");
        Assert.Equal(other, purged.ResourceId);

        factory.PublicObjectStorage.FailingDeleteKey = null;
        var retry = await RunOrphanCleanupAsync(factory);

        Assert.Equal(
            new PaymentProofOrphanCleanupResult(Listed: 1, Recent: 0, Referenced: 0, Orphans: 1, Deleted: 1),
            retry);
        Assert.False(factory.PublicObjectStorage.Exists(failing));
    }

    // Un comprobante v2 ya movido: su FileResource guarda la clave, y la sonda de Storage lo retiene.
    private static async Task<string> MovedProofKeyAsync(HttpClient client, StorageApiFactory factory)
    {
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));
        return publicKey;
    }
}
