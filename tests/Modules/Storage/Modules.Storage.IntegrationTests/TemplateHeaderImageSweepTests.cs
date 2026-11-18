using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.TemplateImages;
using Npgsql;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Spec 2026-11-17, «Closing the residual gaps»: el barrido diario contra Postgres de verdad. Lo que las unitarias no ven: la consulta de
/// candidatas (dueño, estado, gracia de 24 h), el reclamo con <c>FOR UPDATE SKIP LOCKED</c> y que el guardado deja la fila <c>Deleted</c>
/// con su auditoría. Messaging se reemplaza por un doble: el adaptador real lo prueba <c>TemplateImageReferencesTests</c> en Messaging.
/// </summary>
public sealed class TemplateHeaderImageSweepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Un guardado de Storage que falló después de copiar: la fila quedó sin public_storage_key, pero la copia pública existe.
    [Fact]
    public async Task AStaleUnreferencedImageIsReleasedEvenWithoutAStoredKey()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await PublishedTemplateImageAsync(factory, database.GetConnectionString());
        Assert.NotEmpty(factory.PublicObjectStorage.Keys);
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE storage.file_resources SET created_at = now() - interval '2 days', public_storage_key = NULL WHERE id = @id",
            fileId);

        var released = await SweepAsync(factory, referenced: []);

        Assert.Equal(1, released);
        Assert.Equal("Deleted", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.Empty(factory.PublicObjectStorage.Keys);
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");
        Assert.Equal(
            (fileId.ToString(), "file", "template_header_image_unreferenced", "System", (Guid?)TenantId),
            (audit.ResourceId, audit.ResourceType, audit.Outcome, audit.ActorType, audit.TenantId));
    }

    [Fact]
    public async Task AReferencedOrRecentImageIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var referenced = await PublishedTemplateImageAsync(factory, database.GetConnectionString());
        var recent = await PublishedTemplateImageAsync(factory, database.GetConnectionString());
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE storage.file_resources SET created_at = now() - interval '2 days' WHERE id = @id",
            referenced);

        var released = await SweepAsync(factory, referenced: [referenced]);

        Assert.Equal(0, released);
        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), referenced)).Status);
        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), recent)).Status);
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.deleted");
    }

    // Review Focus 4: una fila que otra réplica tiene bloqueada se salta, sin soltarla ni auditarla.
    [Fact]
    public async Task AnImageLockedByAnotherReplicaIsSkipped()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await PublishedTemplateImageAsync(factory, database.GetConnectionString());
        await ExecuteAsync(
            database.GetConnectionString(),
            "UPDATE storage.file_resources SET created_at = now() - interval '2 days' WHERE id = @id",
            fileId);
        await using var otherReplica = new NpgsqlConnection(database.GetConnectionString());
        await otherReplica.OpenAsync(Ct);
        await using var transaction = await otherReplica.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand("SELECT 1 FROM storage.file_resources WHERE id = @id FOR UPDATE", otherReplica, transaction))
        {
            lockRow.Parameters.AddWithValue("id", fileId);
            await lockRow.ExecuteScalarAsync(Ct);
        }

        var released = await SweepAsync(factory, referenced: []);

        Assert.Equal(0, released);
        await transaction.RollbackAsync(Ct);
        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
    }

    private static async Task<int> SweepAsync(StorageApiFactory factory, IReadOnlyCollection<Guid> referenced)
    {
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITemplateImageReferences>();
            services.AddSingleton<ITemplateImageReferences>(new FixedReferences(referenced));
        }));
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITemplateHeaderImageSweep>().SweepAsync(DateTimeOffset.UtcNow, Ct);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, Guid fileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", fileId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    /// <summary>Una imagen de plantilla publicada, como la deja crear (mismo camino que <c>TemplateHeaderImageReleaseTests</c>).</summary>
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

    private sealed class FixedReferences(IReadOnlyCollection<Guid> referenced) : ITemplateImageReferences
    {
        public Task<IReadOnlySet<Guid>> FindReferencedAsync(
            Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<Guid>>(fileIds.Where(referenced.Contains).ToHashSet());
    }
}
