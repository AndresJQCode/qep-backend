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

    /// <summary>Spec 2026-10-25 D-E3: <c>step_number</c> y <c>response_bytes</c> llegan a la fila.</summary>
    [Fact]
    public async Task TheColumnsArePersisted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var runId = Guid.CreateVersion7();

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IHttpCallLog>().RecordAsync(
                new HttpCallEntry(
                    Guid.CreateVersion7(), runId, null, "http1", Guid.CreateVersion7(), "GET", "/customers", 200, 42,
                    HttpCallOutcomes.Ok, null, DateTimeOffset.UtcNow, StepNumber: 7, ResponseBytes: 321),
                Ct);
        }

        var connectionString = database.GetConnectionString();
        Assert.Equal(7, await ScalarAsync<int>(connectionString, $"SELECT step_number FROM automations.http_calls WHERE run_id = '{runId}'"));
        Assert.Equal(321, await ScalarAsync<int>(connectionString, $"SELECT response_bytes FROM automations.http_calls WHERE run_id = '{runId}'"));
    }

    /// <summary>Spec 2026-11-03 D-4/D-5, sólo del lado de la base: no ejercita el ejecutor (el reintento lo prueban las unitarias de
    /// <c>HttpBlockExecutorTests</c>). Prueba que la columna guarda tal cual el código detallado más largo realista (61 caracteres, con los dos
    /// puntos) y que dos filas con el mismo <c>(run_id, node_id, step_number)</c> conviven sin columna nueva ni restricción que lo impida.</summary>
    [Fact]
    public async Task TheColumnTakesTheLongestNetworkCodeAndTwoRowsPerStep()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var tenantId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var connectionId = Guid.CreateVersion7();
        var calledAt = DateTimeOffset.UtcNow;
        const string Detailed = "network:secure_connection_error:protocol_family_not_supported";

        using (var scope = factory.Services.CreateScope())
        {
            var log = scope.ServiceProvider.GetRequiredService<IHttpCallLog>();
            await log.RecordAsync(
                new HttpCallEntry(
                    tenantId, runId, null, "http1", connectionId, "GET", "/Drivers/external", null, 145, HttpCallOutcomes.Network, Detailed,
                    calledAt, StepNumber: 3),
                Ct);
            await log.RecordAsync(
                new HttpCallEntry(
                    tenantId, runId, null, "http1", connectionId, "GET", "/Drivers/external", 200, 80, HttpCallOutcomes.Ok, null,
                    calledAt.AddSeconds(1), StepNumber: 3, ResponseBytes: 12),
                Ct);
        }

        var connectionString = database.GetConnectionString();
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                connectionString, $"SELECT count(*) FROM automations.http_calls WHERE run_id = '{runId}' AND node_id = 'http1' AND step_number = 3"));
        Assert.Equal(
            Detailed,
            await ScalarAsync<string>(
                connectionString, $"SELECT error_code FROM automations.http_calls WHERE run_id = '{runId}' ORDER BY called_at LIMIT 1"));
        Assert.Equal(
            "ok",
            await ScalarAsync<string>(
                connectionString, $"SELECT outcome FROM automations.http_calls WHERE run_id = '{runId}' ORDER BY called_at DESC LIMIT 1"));
    }
}
