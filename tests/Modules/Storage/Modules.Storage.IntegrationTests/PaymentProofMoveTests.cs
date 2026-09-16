using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// El movimiento de un comprobante adjuntado (spec 2026-09-16, D9, paso 4): primero se borra el
/// temporal y después, en un solo SaveChanges, MoveToPublic y el inbox. Un PDF se mueve igual (D1) y
/// un archivo User se ignora (D13).
/// </summary>
public sealed class PaymentProofMoveTests
{
    [Fact]
    public async Task AnAttachedImageProofIsMovedAndItsStagingObjectDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(
            client, factory, "PaymentProof", "comprobante.png", "image/png", await PngAsync(640, 480));
        var publicKey = NewPublicKey(".webp");
        var messageId = await AddAttachedEventAsync(factory, (proof.FileId, publicKey));

        var processed = await RunMoveAsync(factory);

        Assert.Equal(1, processed);
        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal(publicKey, row.PublicStorageKey);
        Assert.Equal("Available", row.Status);
        Assert.Equal(proof.StagingKey, row.StorageKey);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.True(await IsProcessedByMoveAsync(factory, messageId));
    }

    // D1: un PDF se mueve tal cual.
    [Fact]
    public async Task AnAttachedPdfProofIsMovedAsIs()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));

        Assert.Equal(1, await RunMoveAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), proof.FileId);
        Assert.Equal(publicKey, row.PublicStorageKey);
        Assert.Equal("application/pdf", row.MimeType);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
    }

    // D13: un comprobante User conserva su original privado; el mensaje se marca igual.
    [Fact]
    public async Task AUserFileIsIgnored()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var file = await CreateAvailableAsync(client, factory, "User", "comprobante.pdf", "application/pdf", Pdf());
        var privateKey = (await ReadFileAsync(database.GetConnectionString(), file.FileId)).StorageKey;
        var messageId = await AddAttachedEventAsync(factory, (file.FileId, NewPublicKey(".pdf")));

        Assert.Equal(1, await RunMoveAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), file.FileId);
        Assert.Null(row.PublicStorageKey);
        Assert.True(factory.ObjectStorage.Exists(privateKey));
        Assert.True(await IsProcessedByMoveAsync(factory, messageId));
    }

    // D9: si el borrado del temporal falla, no se guarda nada y el mensaje vuelve en el tick siguiente.
    [Fact]
    public async Task WhenDeletingTheStagingObjectFailsNothingIsSavedAndTheMessageComesBack()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        var messageId = await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        factory.ObjectStorage.FailingDeleteKey = proof.StagingKey;

        Assert.Equal(0, await RunMoveAsync(factory));

        Assert.Null((await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
        Assert.False(await IsProcessedByMoveAsync(factory, messageId));
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));

        factory.ObjectStorage.FailingDeleteKey = null;
        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
    }

    // D9: si un tick anterior borró el temporal y no llegó a guardar, el reintento borra una clave que
    // ya no existe —no falla— y guarda.
    [Fact]
    public async Task AStagingObjectAlreadyDeletedDoesNotStopTheMove()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        factory.ObjectStorage.Remove(proof.StagingKey);

        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
    }

    // El inbox: un mensaje ya procesado no se vuelve a aplicar.
    [Fact]
    public async Task AProcessedMessageIsNotAppliedTwice()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Equal(0, await RunMoveAsync(factory));

        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
    }

    // Revisión de Task 6: un mismo request puede adjuntar un archivo como comprobante nuevo y como
    // reemplazo de otro, y el evento lo lista dos veces con claves distintas. El movimiento se queda con
    // la primera y salta la segunda —el recurso ya tiene clave pública—, sin envenenar el mensaje.
    [Fact]
    public async Task AFileListedTwiceWithDifferentKeysIsMovedOnceAndTheMessageIsMarked()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var firstKey = NewPublicKey(".pdf");
        var messageId = await AddAttachedEventAsync(
            factory, (proof.FileId, firstKey), (proof.FileId, NewPublicKey(".pdf")));

        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Equal(firstKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.True(await IsProcessedByMoveAsync(factory, messageId));
    }
}
