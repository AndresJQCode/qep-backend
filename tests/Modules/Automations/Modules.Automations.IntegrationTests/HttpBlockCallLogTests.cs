using Microsoft.Extensions.DependencyInjection;
using Modules.Automations.Application;
using Modules.Automations.Application.Http;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>Spec 2026-10-21 D-H6 contra Postgres: la fila de <c>http_calls</c> se confirma por su cuenta. La llamada a la API ya salió, así
/// que la fila tiene que quedar aunque la unidad del motor que la pidió se deshaga.</summary>
public sealed class HttpBlockCallLogTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRowSurvivesARollbackOfTheUnit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();

        using (var scope = factory.Services.CreateScope())
        {
            var unit = scope.ServiceProvider.GetRequiredService<IAutomationsUnitOfWork>();
            await using var transaction = await unit.BeginTransactionAsync(Ct);
            Assert.True(await unit.TryLockConversationAsync(Guid.CreateVersion7(), Ct));
            // response_too_large (18) no cabía en el varchar(16) de §4.2: la migración WidenHttpCallOutcome lo amplió.
            await scope.ServiceProvider.GetRequiredService<IHttpCallLog>().RecordAsync(
                new HttpCallEntry(
                    tenantId, runId, null, "http1", Guid.CreateVersion7(), "GET", "/customers/" + new string('x', 600), 200, 42,
                    HttpCallOutcomes.ResponseTooLarge, HttpCallOutcomes.ResponseTooLarge, DateTimeOffset.UtcNow),
                Ct);
            // Sin SaveChangesAsync: disponer el handle deshace la unidad.
        }

        var connectionString = database.GetConnectionString();
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"SELECT count(*) FROM automations.http_calls WHERE run_id = '{runId}'"));
        Assert.Equal(
            "response_too_large",
            await ScalarAsync<string>(connectionString, $"SELECT outcome FROM automations.http_calls WHERE run_id = '{runId}'"));
        Assert.Equal(512, await ScalarAsync<int>(connectionString, $"SELECT length(path) FROM automations.http_calls WHERE run_id = '{runId}'"));
    }
}
