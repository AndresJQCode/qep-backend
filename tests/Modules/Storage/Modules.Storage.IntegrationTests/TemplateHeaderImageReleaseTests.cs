using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;
using Modules.Storage.Infrastructure.TemplateImages;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Spec 2026-11-17 («No orphaned images»), plan Task 9: Storage consume el aviso que Messaging escribe cuando la imagen fija del
/// encabezado de una plantilla se queda sin fila. La imagen pierde sus copias públicas, queda <c>Deleted</c> y auditada por el sistema, y
/// el mensaje se marca en el inbox. Un archivo de otro dueño no se toca, uno ya borrado se marca sin más, y un fallo de R2 deja el mensaje
/// para el tick siguiente.
/// </summary>
public sealed class TemplateHeaderImageReleaseTests
{
    private const string Consumer = "storage.template-header-image-release";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReleasedImageIsUnpublishedAndSoftDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await PublishedTemplateImageAsync(factory, database.GetConnectionString());
        var publicKey = Assert.IsType<string>((await ReadFileAsync(database.GetConnectionString(), fileId)).PublicStorageKey);
        Assert.Contains(publicKey, factory.PublicObjectStorage.Keys);
        var messageId = await AddReleasedEventAsync(factory, TenantId, fileId);

        Assert.Equal(1, await RunAsync(factory));

        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Deleted", row.Status);
        Assert.Null(row.PublicStorageKey);
        Assert.Empty(factory.PublicObjectStorage.Keys);
        Assert.True(await IsProcessedAsync(factory, messageId));
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");
        Assert.Equal(
            (fileId.ToString(), "file", "template_header_image_released", "System", (Guid?)TenantId),
            (audit.ResourceId, audit.ResourceType, audit.Outcome, audit.ActorType, audit.TenantId));
    }

    [Fact]
    public async Task AFileOfAnotherOwnerTypeIsNotTouched()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var product = await CreateAvailableAsync(client, factory, "Product", "foto.png", "image/png", await PngAsync(64, 64));
        var messageId = await AddReleasedEventAsync(factory, TenantId, product.FileId);

        Assert.Equal(1, await RunAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), product.FileId)).Status);
        Assert.True(await IsProcessedAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");
    }

    /// <summary>Review Focus 1: con R2 caído el mensaje no se marca (sin fila en el inbox) y vuelve en el tick siguiente.</summary>
    [Fact]
    public async Task ATransientUnpublishFailureLeavesTheMessageForTheNextTick()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await PublishedTemplateImageAsync(factory, database.GetConnectionString());
        var publicKey = Assert.IsType<string>((await ReadFileAsync(database.GetConnectionString(), fileId)).PublicStorageKey);
        var messageId = await AddReleasedEventAsync(factory, TenantId, fileId);
        factory.PublicObjectStorage.FailingDeleteKey = publicKey;

        Assert.Equal(0, await RunAsync(factory));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.False(await IsProcessedAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");

        factory.PublicObjectStorage.FailingDeleteKey = null;

        Assert.Equal(1, await RunAsync(factory));

        Assert.Equal("Deleted", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.Empty(factory.PublicObjectStorage.Keys);
        Assert.True(await IsProcessedAsync(factory, messageId));
    }

    [Fact]
    public async Task AReleaseOfAnAlreadyDeletedFileIsMarkedProcessed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await PublishedTemplateImageAsync(factory, database.GetConnectionString());
        var first = await AddReleasedEventAsync(factory, TenantId, fileId);
        Assert.Equal(1, await RunAsync(factory));
        // El mismo archivo otra vez: la compensación de crear y un borrado pueden avisar los dos.
        var second = await AddReleasedEventAsync(factory, TenantId, fileId);

        Assert.Equal(1, await RunAsync(factory));

        Assert.True(await IsProcessedAsync(factory, first));
        Assert.True(await IsProcessedAsync(factory, second));
        Assert.Equal("Deleted", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");
    }

    [Fact]
    public async Task AnImageOfAnotherTenantOrAMalformedPayloadIsMarkedWithoutTouchingAnything()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await PublishedTemplateImageAsync(factory, database.GetConnectionString());
        var otherTenant = await AddReleasedEventAsync(factory, Guid.CreateVersion7(), fileId);
        var malformed = await AddReleasedPayloadAsync(factory, """{"tenantId":"x"}""");

        Assert.Equal(2, await RunAsync(factory));

        Assert.True(await IsProcessedAsync(factory, otherTenant));
        Assert.True(await IsProcessedAsync(factory, malformed));
        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.NotEmpty(factory.PublicObjectStorage.Keys);
    }

    /// <summary>Una imagen de plantilla publicada, como la deja crear: subida por el camino de sistema con el tenant como dueño, completada
    /// y publicada con <see cref="FilePublication"/>.</summary>
    private static async Task<Guid> PublishedTemplateImageAsync(StorageApiFactory factory, string connectionString)
    {
        var content = await PngAsync(32, 32);
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var uploads = services.GetRequiredService<IStorageSystemUploads>();
        var session = await uploads.OpenAsync(TenantId, FileOwnerType.MessageTemplate, TenantId, "encabezado.png", "image/png", content.Length, 5 * 1024 * 1024, Ct);
        factory.ObjectStorage.Upload((await ReadFileAsync(connectionString, session.FileId)).StorageKey, content);
        Assert.Equal(
            SystemUploadOutcome.Available,
            (await uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.MessageTemplate, TenantId, Ct)).Outcome);
        var resource = await services.GetRequiredService<IFileResourceRepository>().GetAsync(new FileResourceId(session.FileId), Ct);
        await services.GetRequiredService<FilePublication>().PublishAsync(resource!, Ct);
        await services.GetRequiredService<IStorageUnitOfWork>().SaveChangesAsync(Ct);
        return session.FileId;
    }

    /// <summary>El aviso que Messaging escribe (<c>TemplateHeaderImageReleasePublisher</c>), con el nombre y los campos del contrato escritos
    /// a mano.</summary>
    private static Task<Guid> AddReleasedEventAsync(StorageApiFactory factory, Guid tenantId, Guid fileId) =>
        AddReleasedPayloadAsync(factory, JsonSerializer.Serialize(new { tenantId, fileId }));

    private static async Task<Guid> AddReleasedPayloadAsync(StorageApiFactory factory, string payloadJson)
    {
        var id = Guid.CreateVersion7();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        dbContext.Outbox.Add(new StorageOutboxMessage
        {
            Id = id,
            EventName = "messaging.template.header-image-released.v1",
            PayloadJson = payloadJson,
            CorrelationId = id.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(Ct);
        return id;
    }

    /// <summary>Un lote a mano: el hosted service no corre en este host (<see cref="StorageApiFactory"/> lo quita).</summary>
    private static async Task<int> RunAsync(StorageApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITemplateHeaderImageReleaseProcessor>().ProcessPendingAsync(Ct);
    }

    private static async Task<bool> IsProcessedAsync(StorageApiFactory factory, Guid messageId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        return await dbContext.Inbox.AnyAsync(entry => entry.Consumer == Consumer && entry.MessageId == messageId, Ct);
    }
}
