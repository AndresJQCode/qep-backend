using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modules.Messaging.Application;
using Modules.Messaging.Infrastructure.CustomerDeletion;
using static Modules.Messaging.IntegrationTests.MessagingApiHarness;

namespace Modules.Messaging.IntegrationTests;

/// <summary>Spec 2026-11-10 (borrar clientes) A-9: Messaging consume <c>customers.customer.media-released.v1</c> y borra de R2 las claves
/// que el borrado del cliente dejó sin dueño. El borrado ya confirmó: una falla de R2 sólo deja objetos que se reintentan.</summary>
public sealed class CustomerMediaPurgeTests
{
    private const string EventName = "customers.customer.media-released.v1";
    private const string Consumer = "messaging.customer-media-purge";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryKeyOfTheEventIsDeleted()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var bucket = new FakeMessagingBucket();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(bucket)));
        var keys = new[] { "messaging/t/a.jpg", "messaging/t/b.pdf" };
        foreach (var key in keys)
        {
            await bucket.UploadAsync(key, new MemoryStream([1, 2, 3]), 3, "application/octet-stream", Ct);
        }

        await bucket.UploadAsync("messaging/t/other.jpg", new MemoryStream([9]), 1, "image/jpeg", Ct);
        await InsertEventAsync(connectionString, keys);

        var processed = await host.Services.GetRequiredService<ICustomerMediaPurgeProcessor>().ProcessPendingAsync(Ct);

        Assert.Equal(1, processed);
        Assert.Equal(["messaging/t/other.jpg"], bucket.Objects.Keys.ToArray());
        Assert.Equal(1L, await CountAsync(connectionString, $"SELECT count(*) FROM messaging.inbox_messages WHERE consumer = '{Consumer}' AND processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task AFailedDeleteIsRetried()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var bucket = new FailingOnceBucket();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(bucket)));
        await bucket.UploadAsync("messaging/t/a.jpg", new MemoryStream([1]), 1, "image/jpeg", Ct);
        await InsertEventAsync(connectionString, ["messaging/t/a.jpg"]);
        var processor = host.Services.GetRequiredService<ICustomerMediaPurgeProcessor>();

        Assert.Equal(0, await processor.ProcessPendingAsync(Ct));
        Assert.Equal(0L, await CountAsync(connectionString, $"SELECT count(*) FROM messaging.inbox_messages WHERE consumer = '{Consumer}' AND processed_at IS NOT NULL"));
        Assert.Contains("messaging/t/a.jpg", bucket.Objects.Keys);

        // El reclamo del intento fallido vence: el mensaje vuelve al lote.
        await ExecuteAsync(connectionString, $"UPDATE messaging.inbox_messages SET claimed_until = now() - interval '1 minute' WHERE consumer = '{Consumer}'");

        Assert.Equal(1, await processor.ProcessPendingAsync(Ct));
        Assert.Empty(bucket.Objects);
        Assert.Equal(1L, await CountAsync(connectionString, $"SELECT count(*) FROM messaging.inbox_messages WHERE consumer = '{Consumer}' AND processed_at IS NOT NULL"));
    }

    [Fact]
    public async Task AMissingKeyIsNotAnError()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var bucket = new FakeMessagingBucket();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMessagingMediaStore>(bucket)));
        await InsertEventAsync(connectionString, ["messaging/t/never-existed.jpg"]);

        var processed = await host.Services.GetRequiredService<ICustomerMediaPurgeProcessor>().ProcessPendingAsync(Ct);

        Assert.Equal(1, processed);
        Assert.Equal(1L, await CountAsync(connectionString, $"SELECT count(*) FROM messaging.inbox_messages WHERE consumer = '{Consumer}' AND processed_at IS NOT NULL"));
    }

    // La fila que escribe CustomerMediaReleasePublisher en la transacción del borrado.
    private static async Task InsertEventAsync(string connectionString, string[] storageKeys) =>
        Assert.Equal(1, await ExecuteAsync(
            connectionString,
            """
            INSERT INTO platform.outbox_messages (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts)
            VALUES (@id, @eventName, @payload::jsonb, 'customer-media-purge-tests', now(), now(), 0)
            """,
            ("id", Guid.NewGuid()), ("eventName", EventName),
            ("payload", JsonSerializer.Serialize(new { tenantId = Guid.NewGuid(), customerId = Guid.NewGuid(), storageKeys }))));

    /// <summary>R2 caído en el primer borrado: el segundo intento funciona.</summary>
    private sealed class FailingOnceBucket : IMessagingMediaStore
    {
        private bool failed;

        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public async Task UploadAsync(string key, Stream content, long contentLength, string contentType, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Objects[key] = buffer.ToArray();
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            if (!failed)
            {
                failed = true;
                throw new HttpRequestException("R2 no responde.");
            }

            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<MediaStreamDto?> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Uri> CreateDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
