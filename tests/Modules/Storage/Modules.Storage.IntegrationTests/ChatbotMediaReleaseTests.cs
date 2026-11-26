using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.ChatbotMedia;
using Modules.Storage.Infrastructure.Persistence;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Spec 2026-11-18 («Lifecycle»), plan Task 12: Storage consume el aviso que Automations escribe cuando se borra un chatbot con archivos en
/// sus bloques «Mensaje». Cada archivo que ya nadie usa pierde sus copias públicas y queda <c>Deleted</c> y auditado por el sistema; uno que
/// otro chatbot (o la bandeja) todavía muestra se queda. Automations y Messaging se reemplazan por un doble de
/// <see cref="IChatbotMediaReferences"/>: el adaptador real lo prueban <c>ChatbotMediaReferencesTests</c> en Automations.
/// </summary>
public sealed class ChatbotMediaReleaseTests
{
    private const string Consumer = "storage.chatbot-media-release";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReleasedChatbotFileIsUnpublishedAndSoftDeleted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await PublishedChatbotMediaAsync(factory, database.GetConnectionString());
        var publicKey = Assert.IsType<string>((await ReadFileAsync(database.GetConnectionString(), fileId)).PublicStorageKey);
        Assert.Contains(publicKey, factory.PublicObjectStorage.Keys);
        var messageId = await AddReleasedEventAsync(factory, TenantId, fileId);

        Assert.Equal(1, await RunAsync(factory, new FixedReferences([])));

        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal("Deleted", row.Status);
        Assert.Null(row.PublicStorageKey);
        Assert.Empty(factory.PublicObjectStorage.Keys);
        Assert.True(await IsProcessedAsync(factory, messageId));
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");
        Assert.Equal(
            (fileId.ToString(), "file", "chatbot_media_released", "System", (Guid?)TenantId),
            (audit.ResourceId, audit.ResourceType, audit.Outcome, audit.ActorType, audit.TenantId));
    }

    // Dos chatbots comparten el archivo (uno se copió del otro): borrar uno no puede romper el mensaje del otro. El aviso se marca igual.
    [Fact]
    public async Task DeletingOneOfTwoChatbotsSharingAFileKeepsIt()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await PublishedChatbotMediaAsync(factory, database.GetConnectionString());
        var messageId = await AddReleasedEventAsync(factory, TenantId, fileId);
        var references = new FixedReferences([fileId]);

        Assert.Equal(1, await RunAsync(factory, references));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.NotEmpty(factory.PublicObjectStorage.Keys);
        Assert.True(await IsProcessedAsync(factory, messageId));
        Assert.Contains(references.Asked, ids => ids.Contains(fileId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");
    }

    // Con la consulta de uso caída no se suelta nada (falla cerrado) y el mensaje queda sin marcar para el tick siguiente.
    [Fact]
    public async Task AFailingReferenceQueryLeavesTheMessageForTheNextTick()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await PublishedChatbotMediaAsync(factory, database.GetConnectionString());
        var messageId = await AddReleasedEventAsync(factory, TenantId, fileId);
        var references = new FixedReferences([]) { Failing = true };

        Assert.Equal(0, await RunAsync(factory, references));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.NotEmpty(factory.PublicObjectStorage.Keys);
        Assert.False(await IsProcessedAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");

        references.Failing = false;

        Assert.Equal(1, await RunAsync(factory, references));

        Assert.Equal("Deleted", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.Empty(factory.PublicObjectStorage.Keys);
        Assert.True(await IsProcessedAsync(factory, messageId));
    }

    // El aviso sólo habla de archivos de chatbot: el id de una foto de producto (otro dueño) nunca se retira, y el mensaje se marca.
    [Fact]
    public async Task AFileOfAnotherOwnerIsNotTouched()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory);
        var product = await CreateAvailableAsync(client, factory, "Product", "foto.png", "image/png", await PngAsync(64, 64));
        var messageId = await AddReleasedEventAsync(factory, TenantId, product.FileId);

        Assert.Equal(1, await RunAsync(factory, new FixedReferences([])));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), product.FileId)).Status);
        Assert.True(await IsProcessedAsync(factory, messageId));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");
    }

    /// <summary>Un archivo de chatbot publicado, como lo deja completar la subida del editor: por el camino de sistema con el tenant como
    /// dueño, y publicado con <see cref="FilePublication"/>.</summary>
    private static async Task<Guid> PublishedChatbotMediaAsync(StorageApiFactory factory, string connectionString)
    {
        var content = await PngAsync(32, 32);
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var uploads = services.GetRequiredService<IStorageSystemUploads>();
        var session = await uploads.OpenAsync(TenantId, FileOwnerType.ChatbotMedia, TenantId, "catalogo.png", "image/png", content.Length, 25 * 1024 * 1024, Ct);
        factory.ObjectStorage.Upload((await ReadFileAsync(connectionString, session.FileId)).StorageKey, content);
        Assert.Equal(
            SystemUploadOutcome.Available,
            (await uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.ChatbotMedia, TenantId, Ct)).Outcome);
        var resource = await services.GetRequiredService<IFileResourceRepository>().GetAsync(new FileResourceId(session.FileId), Ct);
        await services.GetRequiredService<FilePublication>().PublishAsync(resource!, Ct);
        await services.GetRequiredService<IStorageUnitOfWork>().SaveChangesAsync(Ct);
        return session.FileId;
    }

    /// <summary>El aviso que Automations escribe (<c>ChatbotMediaReleasePublisher</c>), con el nombre y los campos del contrato escritos a
    /// mano.</summary>
    private static async Task<Guid> AddReleasedEventAsync(StorageApiFactory factory, Guid tenantId, params Guid[] fileIds)
    {
        var id = Guid.CreateVersion7();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        dbContext.Outbox.Add(new StorageOutboxMessage
        {
            Id = id,
            EventName = "automations.chatbot.media-released.v1",
            PayloadJson = JsonSerializer.Serialize(new { tenantId, fileIds }),
            CorrelationId = id.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(Ct);
        return id;
    }

    /// <summary>Un lote a mano: el hosted service no corre en este host (<see cref="StorageApiFactory"/> lo quita), con las referencias que
    /// la prueba decide.</summary>
    private static async Task<int> RunAsync(StorageApiFactory factory, FixedReferences references)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatbotMediaReferences>();
            services.AddSingleton<IChatbotMediaReferences>(references);
        }));
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IChatbotMediaReleaseProcessor>().ProcessPendingAsync(Ct);
    }

    private static async Task<bool> IsProcessedAsync(StorageApiFactory factory, Guid messageId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        return await dbContext.Inbox.AnyAsync(entry => entry.Consumer == Consumer && entry.MessageId == messageId, Ct);
    }

    /// <summary>Automations y Messaging de mentira: responde con los ids referenciados y anota por cuáles le preguntaron.</summary>
    private sealed class FixedReferences(IReadOnlyCollection<Guid> referenced) : IChatbotMediaReferences
    {
        public List<IReadOnlyCollection<Guid>> Asked { get; } = [];

        public bool Failing { get; set; }

        public Task<IReadOnlySet<Guid>> FindReferencedAsync(
            Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
        {
            if (Failing)
            {
                throw new InvalidOperationException("The reference query is down.");
            }

            lock (Asked)
            {
                Asked.Add(fileIds.ToList());
            }

            return Task.FromResult<IReadOnlySet<Guid>>(fileIds.Where(referenced.Contains).ToHashSet());
        }
    }
}
