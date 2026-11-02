using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.ApiAccess.Application;
using Modules.ApiAccess.Domain;
using Modules.ApiAccess.Infrastructure;
using Modules.ApiAccess.Infrastructure.Persistence;

namespace Modules.ApiAccess.IntegrationTests;

public sealed class ApiKeyRequestPersistenceTests
{
    private static ApiKeyRequest NewRequest(Guid tenantId, DateTimeOffset occurredAt) =>
        ApiKeyRequest.Record(
            tenantId, Guid.NewGuid(), "ERP", "POST", "/api/v1/external/whatsapp/templates/send",
            statusCode: 201, errorCode: null, messageId: Guid.NewGuid(), traceId: "trace", durationMs: 8, occurredAt);

    [Fact]
    public async Task AFailureToWriteIsAbsorbed()
    {
        // Sin base alcanzable: el INSERT falla y RecordAsync igual vuelve sin lanzar.
        var options = new DbContextOptionsBuilder<ApiAccessDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1")
            .Options;
        var log = new ApiKeyRequestLog(options, NullLogger<ApiKeyRequestLog>.Instance);

        var exception = await Record.ExceptionAsync(() => log.RecordAsync(
            NewRequest(Guid.NewGuid(), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task ThePurgeDeletesOnlyRowsOlderThanFifteenDays()
    {
        var (database, factory) = await ApiAccessApiHarness.OpenAsync();
        await using var _ = database;
        using var __ = factory;
        var connectionString = database.GetConnectionString();
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Arranca el host: la migración del módulo crea la tabla.
        using (factory.CreateClient())
        {
            using var scope = factory.Services.CreateScope();
            var log = scope.ServiceProvider.GetRequiredService<IApiKeyRequestLog>();
            await log.RecordAsync(NewRequest(tenantId, now.AddDays(-16)), TestContext.Current.CancellationToken);
            await log.RecordAsync(NewRequest(tenantId, now.AddDays(-14)), TestContext.Current.CancellationToken);
        }

        var worker = factory.Services.GetServices<IHostedService>()
            .OfType<ApiKeyRequestPurgeWorker>().Single();
        await worker.DrainAsync(TestContext.Current.CancellationToken);

        var remaining = await ApiAccessApiHarness.ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM api_access.api_key_requests WHERE tenant_id = @tenantId AND occurred_at > @cutoff",
            ("tenantId", tenantId),
            ("cutoff", now.AddDays(-15)));
        var total = await ApiAccessApiHarness.ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM api_access.api_key_requests WHERE tenant_id = @tenantId",
            ("tenantId", tenantId));

        Assert.Equal((1L, 1L), (remaining, total));
    }
}
