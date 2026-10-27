using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Automations.Infrastructure.Persistence;
using static Modules.Automations.IntegrationTests.AutomationsApiHarness;

namespace Modules.Automations.IntegrationTests;

/// <summary>La migración <c>AddRunUnits</c> (spec 2026-10-25 §6.1) contra una base con una corrida y una llamada HTTP de antes: las dos
/// sobreviven, y las columnas nuevas de <c>http_calls</c> quedan en NULL. Mismo patrón que <c>SaleLinesVariantMigrationTests</c> de Pos:
/// el host migra todo a la última, la prueba baja Automations con <see cref="IMigrator"/>, siembra y vuelve a subir.</summary>
public sealed class RunUnitsMigrationTests
{
    private const string TenantId = "01900000-0000-7000-8000-00000000a001";
    private const string RunId = "01900000-0000-7000-8000-00000000a002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ExistingRunsAndCallsSurviveAddRunUnits()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        await using (var factory = new QepApiFactory(connectionString))
        {
            _ = factory.Services;
        }

        await using (var context = NewContext(connectionString))
        {
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(
                context.Database.GetMigrations().Single(id => id.EndsWith("_AddRunConsentPrompt", StringComparison.Ordinal)), Ct);
            Assert.Equal(0L, await CountAsync(connectionString,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'automations' AND table_name = 'run_units'"));
            await ExecuteAsync(connectionString, $"""
                INSERT INTO automations.conversation_runs (
                    id, tenant_id, conversation_id, connection_id, chatbot_id, chatbot_version_id, status, step_number, attempts,
                    variables, trace, started_at, updated_at)
                VALUES (
                    '{RunId}', '{TenantId}', '01900000-0000-7000-8000-00000000a003', '01900000-0000-7000-8000-00000000a004',
                    '01900000-0000-7000-8000-00000000a005', '01900000-0000-7000-8000-00000000a006', 'Waiting', 0, 0,
                    jsonb_build_object(), '[]', now(), now());
                INSERT INTO automations.http_calls (
                    tenant_id, run_id, node_id, connection_id, method, path, status_code, duration_ms, outcome, called_at)
                VALUES ('{TenantId}', '{RunId}', 'http1', '01900000-0000-7000-8000-00000000a004', 'GET', '/x', 200, 12, 'ok', now());
                """);

            // Gotcha de CLAUDE.md: MigrateAsync(null) sube a la última; sin argumento no migra.
            await migrator.MigrateAsync(null, Ct);
        }

        Assert.Equal(1L, await CountAsync(connectionString, "SELECT count(*) FROM automations.conversation_runs"));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM automations.http_calls WHERE step_number IS NULL AND response_bytes IS NULL"));
        Assert.Equal(1L, await CountAsync(connectionString,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'automations' AND table_name = 'run_units'"));
    }

    private static AutomationsDbContext NewContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AutomationsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", AutomationsDbContext.Schema))
            .Options);
}
