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

    // Revisión de Task 7 (I1): un mensaje mal formado nunca se va a poder aplicar. Se marca en el inbox
    // en vez de reintentarse cada 3 s; si no, un lote entero de ellos (20, el tamaño del lote del
    // procesador) taparía para siempre a los mensajes válidos que llegan después.
    [Fact]
    public async Task MalformedMessagesAreMarkedAndDoNotBlockALaterValidMessage()
    {
        const int batchSize = 20;
        string[] malformedPayloads =
        [
            """{"orderId":"01900000-0000-7000-8000-0000000000aa","proofs":[]}""",
            """{"tenantId":"not-a-guid","proofs":[]}""",
            """[]""",
        ];
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var malformedIds = new List<Guid>();
        for (var index = 0; index < batchSize; index++)
        {
            malformedIds.Add(await AddAttachedEventPayloadAsync(
                factory, malformedPayloads[index % malformedPayloads.Length]));
        }

        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        var validId = await AddAttachedEventAsync(factory, (proof.FileId, publicKey));

        await RunMoveAsync(factory);
        await RunMoveAsync(factory);

        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
        Assert.True(await IsProcessedByMoveAsync(factory, validId));
        foreach (var malformedId in malformedIds)
        {
            Assert.True(await IsProcessedByMoveAsync(factory, malformedId));
        }
    }

    // Revisión de Task 7 (I1): una entrada sin clave pública se salta sin tocar su temporal, y las
    // entradas válidas del mismo mensaje se mueven igual.
    [Fact]
    public async Task AnEntryWithoutPublicKeyIsSkippedAndTheValidOnesStillMove()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var invalid = await CreateAvailableAsync(client, factory, "PaymentProof", "sin-clave.pdf", "application/pdf", Pdf());
        var valid = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        var messageId = await AddAttachedEventAsync(factory, (invalid.FileId, null), (valid.FileId, publicKey));

        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Null((await ReadFileAsync(database.GetConnectionString(), invalid.FileId)).PublicStorageKey);
        Assert.True(factory.ObjectStorage.Exists(invalid.StagingKey));
        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), valid.FileId)).PublicStorageKey);
        Assert.False(factory.ObjectStorage.Exists(valid.StagingKey));
        Assert.True(await IsProcessedByMoveAsync(factory, messageId));
    }

    // Revisión de Task 7 (I2): un timeout de R2 llega como TaskCanceledException sin que nadie haya
    // pedido apagar el host. Es transitorio: el mensaje no se marca y vuelve en el tick siguiente.
    [Fact]
    public async Task ACancellationThatIsNotAShutdownIsRetriedOnTheNextTick()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        var messageId = await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        factory.ObjectStorage.FailingDeleteKey = proof.StagingKey;
        factory.ObjectStorage.DeleteFailure = new TaskCanceledException("Simulated R2 timeout.");

        Assert.Equal(0, await RunMoveAsync(factory));

        Assert.False(await IsProcessedByMoveAsync(factory, messageId));
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));

        factory.ObjectStorage.FailingDeleteKey = null;
        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
    }

    // Revisión final (I1): el pedido y su evento se guardaron, pero el request recibió un error (la
    // conexión se cortó esperando el COMMIT) y el rollback borró la copia pública. Borrar el temporal
    // dejaría el comprobante sin ninguna copia: la entrada se salta, el temporal y el recurso quedan
    // como estaban, las demás entradas se mueven y el mensaje se marca (reintentar no trae la copia).
    [Fact]
    public async Task AnEntryWhosePublicCopyIsMissingKeepsItsStagingObjectAndTheOthersStillMove()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var missing = await CreateAvailableAsync(client, factory, "PaymentProof", "sin-copia.pdf", "application/pdf", Pdf());
        var valid = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var missingKey = NewPublicKey(".pdf");
        var validKey = NewPublicKey(".pdf");
        var messageId = await AddAttachedEventAsync(factory, (missing.FileId, missingKey), (valid.FileId, validKey));
        factory.PublicObjectStorage.Remove(missingKey);

        Assert.Equal(1, await RunMoveAsync(factory));

        var missingRow = await ReadFileAsync(database.GetConnectionString(), missing.FileId);
        Assert.Null(missingRow.PublicStorageKey);
        Assert.Equal("Available", missingRow.Status);
        Assert.True(factory.ObjectStorage.Exists(missing.StagingKey));
        Assert.Equal(validKey, (await ReadFileAsync(database.GetConnectionString(), valid.FileId)).PublicStorageKey);
        Assert.False(factory.ObjectStorage.Exists(valid.StagingKey));
        Assert.True(await IsProcessedByMoveAsync(factory, messageId));
    }

    // Revisión final (I1): si R2 falla al verificar la copia, no se sabe si existe. Es transitorio: no se
    // borra el temporal, el mensaje no se marca y el tick siguiente lo mueve.
    [Fact]
    public async Task AFailureCheckingThePublicCopyIsRetriedOnTheNextTick()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var publicKey = NewPublicKey(".pdf");
        var messageId = await AddAttachedEventAsync(factory, (proof.FileId, publicKey));
        factory.PublicObjectStorage.FailingExistsKey = publicKey;

        Assert.Equal(0, await RunMoveAsync(factory));

        Assert.Null((await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.False(await IsProcessedByMoveAsync(factory, messageId));

        factory.PublicObjectStorage.FailingExistsKey = null;
        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Equal(publicKey, (await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
        Assert.False(factory.ObjectStorage.Exists(proof.StagingKey));
    }

    // Revisión de Task 7 (M2): un archivo de otro tenant no se toca, y el mensaje se marca igual.
    [Fact]
    public async Task AnEntryWhoseFileBelongsToAnotherTenantIsSkipped()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var proof = await CreateAvailableAsync(client, factory, "PaymentProof", "comprobante.pdf", "application/pdf", Pdf());
        var messageId = await AddAttachedEventForTenantAsync(
            factory, Guid.CreateVersion7(), (proof.FileId, NewPublicKey(".pdf")));

        Assert.Equal(1, await RunMoveAsync(factory));

        Assert.Null((await ReadFileAsync(database.GetConnectionString(), proof.FileId)).PublicStorageKey);
        Assert.True(factory.ObjectStorage.Exists(proof.StagingKey));
        Assert.True(await IsProcessedByMoveAsync(factory, messageId));
    }
}
