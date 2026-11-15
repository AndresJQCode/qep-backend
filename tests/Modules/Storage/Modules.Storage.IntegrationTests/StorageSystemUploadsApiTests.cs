using Microsoft.Extensions.DependencyInjection;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Soporte fase 3 (plan F30, F31 y F36): la subida sin miembro contra la base real. El adjunto de un ticket pasa por el mismo pipeline
/// que cualquier archivo y queda <c>Available</c> en <c>files/</c>; el que nadie reclama lo purga el barrido de staging a las 24 h, y
/// el que una sonda reclama se queda. Un host por prueba: el bucket es un doble y el barrido se corre a mano.
/// </summary>
public sealed class StorageSystemUploadsApiTests
{
    private static readonly TimeSpan OlderThanTheRetention = TimeSpan.FromHours(25);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnUploadWithoutAMemberGoesThroughThePipeline()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var ownerId = Guid.NewGuid();

        var fileId = await UploadAvailableAsync(factory, database.GetConnectionString(), ownerId);

        var row = await ReadFileAsync(database.GetConnectionString(), fileId);
        Assert.Equal(("Available", "SupportTicket"), (row.Status, row.OwnerType));
        Assert.StartsWith("files/", row.StorageKey, StringComparison.Ordinal);
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.uploaded");
        Assert.Equal(("System", "success", fileId.ToString()), (audit.ActorType, audit.Outcome, audit.ResourceId));
        await using var scope = factory.Services.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<IStorageSystemUploads>();
        Assert.NotNull(await uploads.FindAvailableAsync(TenantId, fileId, FileOwnerType.SupportTicket, ownerId, Ct));
    }

    [Fact]
    public async Task AnUnclaimedSupportAttachmentIsPurgedAfterTheRetention()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await UploadAvailableAsync(factory, database.GetConnectionString(), Guid.NewGuid());
        var finalKey = (await ReadFileAsync(database.GetConnectionString(), fileId)).StorageKey;
        await BackdateAsync(database.GetConnectionString(), fileId, OlderThanTheRetention);

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(finalKey));
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
        Assert.Equal(("System", "support_attachment_not_claimed"), (audit.ActorType, audit.Outcome));
    }

    [Fact]
    public async Task AClaimedSupportAttachmentIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await UploadAvailableAsync(factory, database.GetConnectionString(), Guid.NewGuid());
        var finalKey = (await ReadFileAsync(database.GetConnectionString(), fileId)).StorageKey;
        await BackdateAsync(database.GetConnectionString(), fileId, OlderThanTheRetention);
        factory.FileReferences.Reference(fileId);

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(finalKey));
    }

    [Fact]
    public async Task ARecentUnclaimedSupportAttachmentIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await UploadAvailableAsync(factory, database.GetConnectionString(), Guid.NewGuid());

        await RunStagingCleanupAsync(factory);

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
    }

    private static async Task<Guid> UploadAvailableAsync(StorageApiFactory factory, string connectionString, Guid ownerId)
    {
        var content = Pdf();
        await using var scope = factory.Services.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<IStorageSystemUploads>();
        var session = await uploads.OpenAsync(
            TenantId, FileOwnerType.SupportTicket, ownerId, "acta.pdf", "application/pdf", content.Length, 10 * 1024 * 1024, Ct);
        factory.ObjectStorage.Upload((await ReadFileAsync(connectionString, session.FileId)).StorageKey, content);

        var result = await uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.SupportTicket, ownerId, Ct);

        Assert.Equal(SystemUploadOutcome.Available, result.Outcome);
        return session.FileId;
    }
}
