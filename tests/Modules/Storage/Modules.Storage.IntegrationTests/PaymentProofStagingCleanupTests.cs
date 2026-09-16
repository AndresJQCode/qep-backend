using BuildingBlocks.Application;
using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Infrastructure.ObjectStorage;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// El barrido de staging (spec 2026-09-16, D11 y sección 3): sigue purgando las subidas abandonadas,
/// y ahora también el comprobante Available, sin mover y viejo que ningún módulo referencia. Respeta
/// el adjunto que espera su movimiento, el reciente, el movido y los archivos User.
/// </summary>
public sealed class PaymentProofStagingCleanupTests
{
    private static readonly TimeSpan OlderThanTheRetention = TimeSpan.FromHours(25);

    // Lo de siempre, que el procesador nuevo no puede romper.
    [Fact]
    public async Task AnAbandonedUploadIsStillPurged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var abandoned = await UploadAsync(client, factory, "User", "contrato.pdf", "application/pdf", Pdf());
        await BackdateAsync(database.GetConnectionString(), abandoned.FileId, OlderThanTheRetention);

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), abandoned.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(abandoned.StagingKey));
    }

    [Fact]
    public async Task AnUnattachedProofOlderThanTheRetentionIsPurged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        await BackdateAsync(database.GetConnectionString(), proof.FileId, OlderThanTheRetention);

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
        Assert.Equal(proof.FileId.ToString(), audit.ResourceId);
        Assert.Equal("file", audit.ResourceType);
        Assert.Equal("payment_proof_not_attached", audit.Outcome);
        Assert.Equal("System", audit.ActorType);
        Assert.Equal(TenantId, audit.TenantId);
    }

    // Adjunto, con el evento de D9 todavía sin procesar: la sonda lo retiene.
    [Fact]
    public async Task AnAttachedProofWaitingToBeMovedIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        await BackdateAsync(database.GetConnectionString(), proof.FileId, OlderThanTheRetention);
        factory.FileReferences.Reference(proof.FileId);

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    [Fact]
    public async Task ARecentUnattachedProofIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
    }

    [Fact]
    public async Task AMovedProofIsNotPurged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));
        await BackdateAsync(database.GetConnectionString(), proof.FileId, OlderThanTheRetention);

        await RunStagingCleanupAsync(factory);

        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal("Available", row.Status);
        Assert.Equal(publicKey, row.PublicStorageKey);
    }

    // D13: un archivo User disponible no es asunto del barrido.
    [Fact]
    public async Task AUserFileIsNotSwept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var file = await CreateAvailableAsync(client, factory, "User", "comprobante.pdf", "application/pdf", Pdf());
        await BackdateAsync(database.GetConnectionString(), file.FileId, OlderThanTheRetention);

        await RunStagingCleanupAsync(factory);

        var row = await ReadFileAsync(database.GetConnectionString(), file.FileId);
        Assert.Equal("Available", row.Status);
        Assert.True(factory.ObjectStorage.Exists(row.StorageKey));
    }

    // Guardar por archivo: si el borrado de uno falla, los purgados antes ya quedaron guardados, los
    // que siguen se purgan igual y el que falla sigue Available con su objeto. Sin esto, un fallo a
    // mitad de lote dejaba comprobantes Available sin objeto en staging/.
    [Fact]
    public async Task AFailingProofDoesNotStopTheOthersNorLeaveThemWithoutTheirObject()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var first = await CreateAvailableAsync(client, factory, "PaymentProof", "primero.pdf", "application/pdf", Pdf());
        var failing = await CreateAvailableAsync(client, factory, "PaymentProof", "falla.pdf", "application/pdf", Pdf());
        var last = await CreateAvailableAsync(client, factory, "PaymentProof", "ultimo.pdf", "application/pdf", Pdf());
        // El orden del barrido es por CreatedAt: el que falla queda en el medio.
        await BackdateAsync(database.GetConnectionString(), first.FileId, TimeSpan.FromHours(27));
        await BackdateAsync(database.GetConnectionString(), failing.FileId, TimeSpan.FromHours(26));
        await BackdateAsync(database.GetConnectionString(), last.FileId, TimeSpan.FromHours(25));
        factory.ObjectStorage.FailingDeleteKey = failing.StagingKey;

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), first.FileId)).Status);
        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), last.FileId)).Status);
        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), failing.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(failing.StagingKey));
        var purged = (await AuditEventsAsync(factory))
            .Where(entry => entry.Action == "storage.file.purged")
            .Select(entry => entry.ResourceId)
            .Order()
            .ToArray();
        Assert.Equal(new[] { first.FileId.ToString(), last.FileId.ToString() }.Order().ToArray(), purged);
    }

    // Sin ninguna sonda registrada no se puede saber si un comprobante está adjunto: no se purga
    // ninguno. Las subidas abandonadas no dependen de las sondas y se siguen purgando.
    [Fact]
    public async Task WithoutProbesNoProofIsPurgedButAbandonedUploadsAre()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var abandoned = await UploadAsync(client, factory, "User", "contrato.pdf", "application/pdf", Pdf());
        await BackdateAsync(database.GetConnectionString(), proof.FileId, OlderThanTheRetention);
        await BackdateAsync(database.GetConnectionString(), abandoned.FileId, OlderThanTheRetention);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var processor = ActivatorUtilities.CreateInstance<StagingCleanupProcessor>(
                scope.ServiceProvider, new List<IFileReferenceProbe>());
            await processor.CleanupAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), abandoned.FileId)).Status);
    }
}
