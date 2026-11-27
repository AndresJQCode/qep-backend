using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Storage.Application;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.HrDocuments;
using Modules.Storage.Infrastructure.Persistence;
using Npgsql;
using static Modules.Storage.IntegrationTests.PaymentProofStorageHarness;

namespace Modules.Storage.IntegrationTests;

/// <summary>
/// Spec 2026-11-26 («PDF del registro eliminado» y «Pruebas»): Storage consume el aviso que Hr escribe al eliminar un registro con PDF,
/// contra Storage real. Borra el objeto y deja la fila <c>Purged</c> y auditada; conserva el PDF que Hr todavía usa; tras una falla de R2
/// el mensaje queda reclamado sin terminar (<c>attempts</c>, <c>claimed_until</c>) y el reintento lo purga; y dos réplicas no purgan dos
/// veces. Hr se reemplaza por un doble de <see cref="IHrDocumentReferences"/>: el adaptador real lo prueba <c>HrDocumentReferencesTests</c>
/// en Hr.
/// </summary>
public sealed class HrDocumentReleaseTests
{
    private const string Consumer = "storage.hr-document-release";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReleasedHrDocumentIsPurged()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await AvailableHrDocumentAsync(factory, database.GetConnectionString());
        var storageKey = (await ReadFileAsync(database.GetConnectionString(), fileId)).StorageKey;
        Assert.True(factory.ObjectStorage.Exists(storageKey));
        var messageId = await AddReleasedEventAsync(factory, TenantId, fileId);
        var references = new FixedReferences([]);

        Assert.Equal(1, await RunAsync(factory, references));

        Assert.False(factory.ObjectStorage.Exists(storageKey));
        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        var inbox = await InboxAsync(database.GetConnectionString(), messageId);
        Assert.True(inbox.Processed);
        Assert.Equal(1, inbox.Attempts);
        var audit = Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
        Assert.Equal(
            (fileId.ToString(), "file", "hr_document_released", "System", (Guid?)TenantId),
            (audit.ResourceId, audit.ResourceType, audit.Outcome, audit.ActorType, audit.TenantId));
        Assert.Contains(references.Asked, ids => ids.SequenceEqual([fileId]));
    }

    [Fact]
    public async Task AReferencedHrDocumentIsKept()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await AvailableHrDocumentAsync(factory, database.GetConnectionString());
        var storageKey = (await ReadFileAsync(database.GetConnectionString(), fileId)).StorageKey;
        var messageId = await AddReleasedEventAsync(factory, TenantId, fileId);

        Assert.Equal(1, await RunAsync(factory, new FixedReferences([fileId])));

        Assert.Equal("Available", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(storageKey));
        Assert.True((await InboxAsync(database.GetConnectionString(), messageId)).Processed);
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    [Fact]
    public async Task AnR2FailureIsRetriedWithItsClaim()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new StorageApiFactory(connectionString);
        var fileId = await AvailableHrDocumentAsync(factory, connectionString);
        var storageKey = (await ReadFileAsync(connectionString, fileId)).StorageKey;
        var messageId = await AddReleasedEventAsync(factory, TenantId, fileId);
        var references = new FixedReferences([]);
        factory.ObjectStorage.FailingDeleteKey = storageKey;
        var beforeClaim = DateTimeOffset.UtcNow;

        Assert.Equal(0, await RunAsync(factory, references));

        // El primer reclamo dura 5 s desde que se tomó: la espera antes del reintento.
        var claimed = await InboxAsync(connectionString, messageId);
        Assert.False(claimed.Processed);
        Assert.Equal(1, claimed.Attempts);
        Assert.True(claimed.ClaimedUntil >= beforeClaim.AddSeconds(4), $"claimed_until {claimed.ClaimedUntil:O}, before {beforeClaim:O}");
        // La marca quedó confirmada: la fila es Deleted (nunca Available sin objeto) y, entretanto, un adjunto de Hr no la encuentra.
        Assert.Equal("Deleted", (await ReadFileAsync(connectionString, fileId)).Status);
        Assert.True(factory.ObjectStorage.Exists(storageKey));
        Assert.DoesNotContain(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var attach = await scope.ServiceProvider.GetRequiredService<IStorageSystemUploads>()
                .CompleteAsync(TenantId, fileId, FileOwnerType.HrDocument, TenantId, Ct);
            Assert.Equal(SystemUploadOutcome.NotFound, attach.Outcome);
        }

        factory.ObjectStorage.FailingDeleteKey = null;
        await ExecuteAsync(
            connectionString,
            $"UPDATE storage.inbox_messages SET claimed_until = now() - interval '1 second' WHERE consumer = '{Consumer}' AND message_id = '{messageId}'");

        Assert.Equal(1, await RunAsync(factory, references));

        var finished = await InboxAsync(connectionString, messageId);
        Assert.True(finished.Processed);
        Assert.Equal(2, finished.Attempts);
        Assert.Equal("Purged", (await ReadFileAsync(connectionString, fileId)).Status);
        Assert.False(factory.ObjectStorage.Exists(storageKey));
        Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
    }

    // Spec 2026-11-26, «Por cada id», paso 3: desde el commit de la marca, un adjunto que lea Storage recibe NotFound (en Hr,
    // hr.file.not_found). El doble de Hr hace, en cada pregunta, lo que hace el adjunto (CompleteAsync en un scope propio, sin candado).
    [Fact]
    public async Task AnAttachAfterTheMarkDoesNotFindTheFile()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await AvailableHrDocumentAsync(factory, database.GetConnectionString());
        await AddReleasedEventAsync(factory, TenantId, fileId);
        var outcomes = new List<SystemUploadOutcome>();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHrDocumentReferences>();
            services.AddScoped<IHrDocumentReferences>(provider =>
                new ProbingReferences(provider.GetRequiredService<IServiceScopeFactory>(), outcomes));
        }));

        Assert.Equal(1, await host.Services.GetRequiredService<IHrDocumentReleaseProcessor>().ProcessPendingAsync(Ct));

        Assert.Equal([SystemUploadOutcome.Available, SystemUploadOutcome.NotFound], outcomes);
        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
    }

    [Fact]
    public async Task TwoReplicasPurgeOnce()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new StorageApiFactory(database.GetConnectionString());
        var fileId = await AvailableHrDocumentAsync(factory, database.GetConnectionString());
        var messageId = await AddReleasedEventAsync(factory, TenantId, fileId);
        using var host = WithReferences(factory, new FixedReferences([]));
        var scopes = host.Services.GetRequiredService<IServiceScopeFactory>();
        var first = new HrDocumentReleaseProcessor(scopes, NullLogger<HrDocumentReleaseProcessor>.Instance);
        var second = new HrDocumentReleaseProcessor(scopes, NullLogger<HrDocumentReleaseProcessor>.Instance);

        var processed = await Task.WhenAll(first.ProcessPendingAsync(Ct), second.ProcessPendingAsync(Ct));

        Assert.Equal(1, processed.Sum());
        Assert.Equal("Purged", (await ReadFileAsync(database.GetConnectionString(), fileId)).Status);
        Assert.Single(await AuditEventsAsync(factory), entry => entry.Action == "storage.file.purged");
        var inbox = await InboxAsync(database.GetConnectionString(), messageId);
        Assert.True(inbox.Processed);
        Assert.Equal(1, inbox.Attempts);
    }

    /// <summary>Un PDF de RRHH <c>Available</c>, como lo deja completar la subida de Hr (<c>HrDocumentStore</c>): por el camino de sistema
    /// con el tenant como dueño. Hr no lo publica nunca.</summary>
    private static async Task<Guid> AvailableHrDocumentAsync(StorageApiFactory factory, string connectionString)
    {
        var content = Pdf();
        await using var scope = factory.Services.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<IStorageSystemUploads>();
        var session = await uploads.OpenAsync(
            TenantId, FileOwnerType.HrDocument, TenantId, "contrato.pdf", "application/pdf", content.Length, 10 * 1024 * 1024, Ct);
        factory.ObjectStorage.Upload((await ReadFileAsync(connectionString, session.FileId)).StorageKey, content);
        Assert.Equal(
            SystemUploadOutcome.Available,
            (await uploads.CompleteAsync(TenantId, session.FileId, FileOwnerType.HrDocument, TenantId, Ct)).Outcome);
        return session.FileId;
    }

    /// <summary>El aviso que Hr escribe (<c>HrDocumentReleasePublisher</c>), con el nombre y los campos del contrato escritos a mano.</summary>
    private static async Task<Guid> AddReleasedEventAsync(StorageApiFactory factory, Guid tenantId, params Guid[] fileIds)
    {
        var id = Guid.CreateVersion7();
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        dbContext.Outbox.Add(new StorageOutboxMessage
        {
            Id = id,
            EventName = "hr.employee.documents-released.v1",
            PayloadJson = JsonSerializer.Serialize(new { tenantId, fileIds }),
            CorrelationId = id.ToString(),
            OccurredAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(Ct);
        return id;
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithReferences(
        StorageApiFactory factory, FixedReferences references) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHrDocumentReferences>();
            services.AddSingleton<IHrDocumentReferences>(references);
        }));

    /// <summary>Un lote a mano: el hosted service no corre en este host (<see cref="StorageApiFactory"/> lo quita), con las referencias que
    /// la prueba decide.</summary>
    private static async Task<int> RunAsync(StorageApiFactory factory, FixedReferences references)
    {
        using var host = WithReferences(factory, references);
        return await host.Services.GetRequiredService<IHrDocumentReleaseProcessor>().ProcessPendingAsync(Ct);
    }

    private static async Task<(bool Processed, int Attempts, DateTimeOffset? ClaimedUntil)> InboxAsync(string connectionString, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT processed_at IS NOT NULL, attempts, claimed_until FROM storage.inbox_messages WHERE consumer = @consumer AND message_id = @id",
            connection);
        command.Parameters.AddWithValue("consumer", Consumer);
        command.Parameters.AddWithValue("id", messageId);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetBoolean(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    /// <summary>Hr de mentira que, en cada pregunta, intenta adjuntar el archivo como lo hace Hr (<c>HrDocumentStore.CompleteAsync</c>, en
    /// su propio scope y sin candado), anota lo que Storage le respondió y contesta que nadie lo usa.</summary>
    private sealed class ProbingReferences(IServiceScopeFactory scopes, List<SystemUploadOutcome> outcomes) : IHrDocumentReferences
    {
        public async Task<IReadOnlySet<Guid>> FindReferencedAsync(
            Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
        {
            foreach (var fileId in fileIds)
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IStorageSystemUploads>()
                    .CompleteAsync(tenantId, fileId, FileOwnerType.HrDocument, tenantId, cancellationToken);
                lock (outcomes)
                {
                    outcomes.Add(result.Outcome);
                }
            }

            return new HashSet<Guid>();
        }
    }

    /// <summary>Hr de mentira: responde con los ids que la prueba dice en uso y anota por cuáles le preguntaron.</summary>
    private sealed class FixedReferences(IReadOnlyCollection<Guid> referenced) : IHrDocumentReferences
    {
        public List<IReadOnlyCollection<Guid>> Asked { get; } = [];

        public Task<IReadOnlySet<Guid>> FindReferencedAsync(
            Guid tenantId, IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(fileIds.ToList());
            }

            return Task.FromResult<IReadOnlySet<Guid>>(fileIds.Where(referenced.Contains).ToHashSet());
        }
    }
}
