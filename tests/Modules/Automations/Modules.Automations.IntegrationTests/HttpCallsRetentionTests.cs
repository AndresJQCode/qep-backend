using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application.Http;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 D-H6: <c>automations.http_calls</c> guarda 30 días (<c>Automations:Workers:HttpCallRetentionDays</c>). La purga
/// corre en el barrido del worker, junto al abandono y a la purga del inbox.</summary>
public sealed class HttpCallsRetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CallsOlderThanThirtyDaysArePurged()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.CreateVersion7();
        using (var scope = factory.Services.CreateScope())
        {
            var log = scope.ServiceProvider.GetRequiredService<IHttpCallLog>();
            foreach (var (path, calledAt) in new[] { ("/old", now.AddDays(-31)), ("/recent", now.AddDays(-1)) })
            {
                await log.RecordAsync(
                    new HttpCallEntry(tenantId, Guid.CreateVersion7(), null, "http1", Guid.CreateVersion7(), "GET", path, 200, 12, HttpCallOutcomes.Ok, null, calledAt),
                    Ct);
            }
        }

        await SweepAutomationsAsync(factory, now);

        Assert.Equal("/recent", await ScalarAsync<string>(connectionString, "SELECT string_agg(path, ',') FROM automations.http_calls"));
    }
}
