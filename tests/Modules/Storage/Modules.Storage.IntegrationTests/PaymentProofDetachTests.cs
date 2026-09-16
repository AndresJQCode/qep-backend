using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Infrastructure.PaymentProofs;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// El retiro de un comprobante que su pedido soltó (spec 2026-09-16, D19): uno movido pierde su copia
/// pública y uno en staging/ su temporal y la copia que alcanzó a hacer el adjunto; los dos quedan
/// Purged y auditados. Se respeta el que otro pedido todavía usa, cada mensaje se aplica una vez, un
/// borrado fallido no guarda nada, un archivo User conserva su original y el movimiento salta lo que ya
/// se purgó.
/// </summary>
public sealed class PaymentProofDetachTests
{
    [Fact]
    public async Task AMovedProofIsPurgedAndItsPublicObjectDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);
        var messageId = await AddDetachedEventAsync(factory, (fileId, publicKey));

        Assert.Equal(1, await RunDetachAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Purged", row.Status);
        Assert.False(factory.PublicObjectStorage.Exists(publicKey));
        Assert.Equal([publicKey], factory.PublicObjectStorage.DeletedKeys);
        Assert.True(await IsProcessedByDetachAsync(factory, messageId));
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
        Assert.Equal(fileId.ToString(), audit.ResourceId);
        Assert.Equal("file", audit.ResourceType);
        Assert.Equal("payment_proof_detached", audit.Outcome);
        Assert.Equal("System", audit.ActorType);
        Assert.Equal(TenantId, audit.TenantId);
    }

    // D19 con Quotations:PaymentProofs:PublicLinks apagada: sin copia, sólo el temporal.
    [Fact]
    public async Task AStagedProofWithoutACopyIsPurgedWithItsStagingObject()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var messageId = await AddDetachedEventAsync(factory, (proof.FileId, null));

        Assert.Equal(1, await RunDetachAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal("Purged", row.Status);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.True(await IsProcessedByDetachAsync(factory, messageId));
    }

    // La carrera de D19: el comprobante se suelta antes de que se procese su adjunto. Se borran el
    // temporal y la copia que el adjunto alcanzó a hacer, y el movimiento, que llega después, lo salta
    // sin fallar y marca su mensaje.
    [Fact]
    public async Task AProofDetachedBeforeItsMoveLosesItsCopyAndTheMoveSkipsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(publicKey, DateTimeOffset.UtcNow);
        var attachedId = await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        await AddDetachedEventAsync(factory, (proof.FileId, publicKey));

        Assert.Equal(1, await RunDetachAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal("Purged", row.Status);
        Assert.Null(row.PublicStorageKey);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.False(factory.PublicObjectStorage.Exists(publicKey));

        Assert.Equal(1, await RunMoveAsync(factory));

        row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal("Purged", row.Status);
        Assert.Null(row.PublicStorageKey);
        Assert.True(await IsProcessedByMoveAsync(factory, attachedId));
    }

    // Otro pedido todavía lo usa: no se borra nada y el mensaje se marca igual.
    [Fact]
    public async Task AProofStillReferencedIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);
        factory.FileReferences.Reference(fileId);
        var messageId = await AddDetachedEventAsync(factory, (fileId, publicKey));

        Assert.Equal(1, await RunDetachAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Available", row.Status);
        Assert.Equal(publicKey, row.PublicStorageKey);
        Assert.True(factory.PublicObjectStorage.Exists(publicKey));
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.True(await IsProcessedByDetachAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    // El inbox no repite un mensaje, y un segundo mensaje por un archivo ya purgado no borra ni audita
    // de nuevo.
    [Fact]
    public async Task ADetachIsAppliedOnlyOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);
        await AddDetachedEventAsync(factory, (fileId, publicKey));
        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal(0, await RunDetachAsync(factory));
        await AddDetachedEventAsync(factory, (fileId, publicKey));
        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.Equal([publicKey], factory.PublicObjectStorage.DeletedKeys);
        Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    // Mismo orden que D9: si un borrado falla no se guarda nada y el mensaje vuelve en el tick siguiente.
    [Fact]
    public async Task WhenADeleteFailsNothingIsSavedAndTheMessageComesBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var messageId = await AddDetachedEventAsync(factory, (proof.FileId, null));
        factory.ObjectStorage.FailingDeleteKey = proof.StagingKey;

        Assert.Equal(0, await RunDetachAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.False(await IsProcessedByDetachAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");

        factory.ObjectStorage.FailingDeleteKey = null;
        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
    }

    // D13 y D19: un comprobante User conserva su original privado; sólo se borra la copia de ese
    // adjunto, que era única.
    [Fact]
    public async Task AUserFileKeepsItsOriginalAndLosesOnlyTheCopyOfThatAttachment()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var file = await CreateAvailableAsync(client, factory, "User", "comprobante.pdf", "application/pdf", Pdf());
        var privateKey = (await ReadFileAsync(database.GetConnectionString(), file.FileId)).StorageKey;
        var copyKey = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(copyKey, DateTimeOffset.UtcNow);
        await AddDetachedEventAsync(factory, (file.FileId, copyKey));

        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), file.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(privateKey));
        Assert.False(factory.PublicObjectStorage.Exists(copyKey));
        var audit = Assert.Single(
            await AuditEventsAsync(factory), entry => entry.Action == "storage.public_object.purged");
        Assert.Equal(copyKey, audit.ResourceId);
        Assert.Equal("public_object", audit.ResourceType);
        Assert.Equal("payment_proof_detached", audit.Outcome);
        Assert.Equal(TenantId, audit.TenantId);
    }

    // Revisión de Task 7 (I1), aplicada al retiro: un mensaje mal formado nunca se va a poder aplicar.
    // Se marca en vez de reintentarse cada 3 s, y un lote entero de ellos no tapa al válido que llega
    // después.
    [Fact]
    public async Task MalformedMessagesAreMarkedAndDoNotBlockALaterValidMessage()
    {
        const int batchSize = 20;
        string[] malformedPayloads =
        [
            """{"orderId":"01900000-0000-7000-8000-0000000000aa","proofs":[]}""",
            """{"tenantId":"not-a-guid","proofs":[]}""",
            """[]""",
            """{"tenantId":"01900000-0000-7000-8000-000000000001","proofs":[{"publicStorageKey":null}]}""",
        ];
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var malformedIds = new List<Guid>();
        for (var index = 0; index < batchSize; index++)
        {
            malformedIds.Add(await AddDetachedEventPayloadAsync(
                factory, malformedPayloads[index % malformedPayloads.Length]));
        }

        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var validId = await AddDetachedEventAsync(factory, (proof.FileId, null));

        await RunDetachAsync(factory);
        await RunDetachAsync(factory);

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.True(await IsProcessedByDetachAsync(factory, validId));
        foreach (var malformedId in malformedIds)
        {
            Assert.True(await IsProcessedByDetachAsync(factory, malformedId));
        }
    }

    // Un mensaje con varios archivos cuyo segundo borrado falla: el primero queda purgado y guardado
    // —no Available sin objeto—, el mensaje vuelve, y el reintento salta el ya purgado y termina el
    // resto sin auditarlo dos veces.
    [Fact]
    public async Task AFailureMidMessageKeepsWhatWasPurgedAndTheRetryFinishesTheRest()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var first = await CreateAvailableAsync(client, factory, "PaymentProof", "primero.pdf", "application/pdf", Pdf());
        var second = await CreateAvailableAsync(client, factory, "PaymentProof", "segundo.pdf", "application/pdf", Pdf());
        var messageId = await AddDetachedEventAsync(factory, (first.FileId, null), (second.FileId, null));
        factory.ObjectStorage.FailingDeleteKey = second.StagingKey;

        Assert.Equal(0, await RunDetachAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), first.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(first.StagingKey));
        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), second.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(second.StagingKey));
        Assert.False(await IsProcessedByDetachAsync(factory, messageId));

        factory.ObjectStorage.FailingDeleteKey = null;
        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), second.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(second.StagingKey));
        Assert.True(await IsProcessedByDetachAsync(factory, messageId));
        var purged = (await AuditEventsAsync(factory))
            .Where(entry => entry.Action == "storage.file.purged")
            .Select(entry => entry.ResourceId)
            .Order()
            .ToArray();
        Assert.Equal(new[] { first.FileId.ToString(), second.FileId.ToString() }.Order().ToArray(), purged);
    }

    // Sin ninguna sonda registrada no se puede saber si otro pedido usa el archivo: no se purga nada y
    // el mensaje queda pendiente, así se aplica cuando haya sondas.
    [Fact]
    public async Task WithoutProbesNothingIsPurgedAndTheMessageWaits()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var messageId = await AddDetachedEventAsync(factory, (proof.FileId, null));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var processor = ActivatorUtilities.CreateInstance<PaymentProofDetachProcessor>(
                scope.ServiceProvider, new List<IFileReferenceProbe>());
            Assert.Equal(0, await processor.ProcessPendingAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.False(await IsProcessedByDetachAsync(factory, messageId));

        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
    }

    // Revisión de Task 7 (I2), aplicada al retiro: un timeout de R2 llega como TaskCanceledException sin
    // que nadie haya pedido apagar el host. Es transitorio: el mensaje no se marca y vuelve.
    [Fact]
    public async Task ACancellationThatIsNotAShutdownIsRetriedOnTheNextTick()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var messageId = await AddDetachedEventAsync(factory, (proof.FileId, null));
        factory.ObjectStorage.FailingDeleteKey = proof.StagingKey;
        factory.ObjectStorage.DeleteFailure = new TaskCanceledException("Simulated R2 timeout.");

        Assert.Equal(0, await RunDetachAsync(factory));

        Assert.False(await IsProcessedByDetachAsync(factory, messageId));
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));

        factory.ObjectStorage.FailingDeleteKey = null;
        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
    }

    // Revisión de Task 7 (M2), aplicada al retiro: un archivo de otro tenant no se toca, y el mensaje
    // se marca igual.
    [Fact]
    public async Task AnEntryWhoseFileBelongsToAnotherTenantIsSkipped()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var (fileId, publicKey) = await MovedProofAsync(client, factory);
        var messageId = await AddDetachedEventForTenantAsync(factory, Guid.CreateVersion7(), (fileId, publicKey));

        Assert.Equal(1, await RunDetachAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.True(factory.PublicObjectStorage.Exists(publicKey));
        Assert.Empty(factory.PublicObjectStorage.DeletedKeys);
        Assert.True(await IsProcessedByDetachAsync(factory, messageId));
    }

    // Un comprobante v2 ya movido, con su copia en el bucket público en memoria.
    private static async Task<(Guid FileId, string PublicKey)> MovedProofAsync(
        HttpClient client, StorageApiFactory factory)
    {
        var proof = await CreateAvailableAsync(
            client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        factory.PublicObjectStorage.Put(publicKey, DateTimeOffset.UtcNow);
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));
        return (proof.FileId, publicKey);
    }
}
