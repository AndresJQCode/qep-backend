using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Support.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// The backfill of <c>AddSupportInternalTasks</c> (spec 2026-11-22 §5.7): each ticket's creator comes from its <c>Created</c> event,
/// and each provisioned tenant gets one internal type after its last. Its own database: Support alone is migrated up to the migration
/// before, the old rows are seeded by SQL, and the migration under test runs on them.
/// </summary>
public sealed class SupportInternalTasksBackfillTests(SupportInternalTasksBackfillTests.BackfilledDatabase database)
    : IClassFixture<SupportInternalTasksBackfillTests.BackfilledDatabase>
{
    private static readonly Guid TenantWithTickets = Guid.Parse("0193a000-0000-7000-8000-00000000d001");
    private static readonly Guid TenantWithItsOwnTask = Guid.Parse("0193a000-0000-7000-8000-00000000d002");
    private static readonly Guid TenantWithoutTypes = Guid.Parse("0193a000-0000-7000-8000-00000000d003");
    private static readonly Guid Actor = Guid.Parse("0193a000-0000-7000-8000-0000000000a1");
    private static readonly Guid ManualTicket = Guid.Parse("0193a000-0000-7000-8000-00000000e001");
    private static readonly Guid PublicTicket = Guid.Parse("0193a000-0000-7000-8000-00000000e002");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCreatorComesFromTheCreatedEvent()
    {
        Assert.Equal(Actor, await database.CreatorAsync(ManualTicket));
        Assert.Null(await database.CreatorAsync(PublicTicket));
    }

    [Fact]
    public async Task EveryProvisionedTenantGetsOneInternalTypeAfterItsLast()
    {
        var withTickets = Assert.Single(await database.InternalTypesAsync(TenantWithTickets));
        Assert.Equal(("Tarea interna", 9), withTickets);

        var withoutTypes = Assert.Single(await database.InternalTypesAsync(TenantWithoutTypes));
        Assert.Equal(("Tarea interna", 1), withoutTypes);
    }

    [Fact]
    public async Task ATenantWithItsOwnTareaInternaTypeStillGetsTheInternalOne()
    {
        var internalType = Assert.Single(await database.InternalTypesAsync(TenantWithItsOwnTask));
        Assert.Equal(("Tarea interna", 4), internalType);

        // Its own type keeps its row and stays external (D8: the name index only covers the external ones).
        Assert.Equal(
            1L,
            await database.ScalarAsync(
                $"SELECT count(*) FROM support.ticket_types WHERE tenant_id = '{TenantWithItsOwnTask}' AND name = 'Tarea interna' AND NOT is_internal"));
    }

    /// <summary>One container for the class: up to <c>AddSupportAutoCloseAndSurveyIndexes</c>, the old rows, then the migration.</summary>
    public sealed class BackfilledDatabase : IAsyncLifetime
    {
        private PostgreSqlContainer? _container;

        private string ConnectionString =>
            _container?.GetConnectionString() ?? throw new InvalidOperationException("The database has not been started.");

        public async ValueTask InitializeAsync()
        {
            _container = await SupportApiHarness.StartDatabaseAsync();
            await using var context = new SupportDbContext(
                new DbContextOptionsBuilder<SupportDbContext>()
                    .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SupportDbContext.Schema))
                    .Options);
            // AddSupportFormOrganization puts a real FK on companies.companies, which Companies creates in the real host.
            await context.Database.ExecuteSqlRawAsync(
                "CREATE SCHEMA companies; CREATE TABLE companies.companies (id uuid PRIMARY KEY);", Ct);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("AddSupportAutoCloseAndSurveyIndexes", Ct);
            await SeedOldRowsAsync();
            await migrator.MigrateAsync("AddSupportInternalTasks", Ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_container is not null)
            {
                await _container.DisposeAsync();
            }
        }

        public async Task<Guid?> CreatorAsync(Guid ticketId)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand("SELECT created_by_member_id FROM support.tickets WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", ticketId);
            var value = await command.ExecuteScalarAsync(Ct);
            return value is Guid guid ? guid : null;
        }

        public async Task<List<(string Name, int Position)>> InternalTypesAsync(Guid tenant)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "SELECT name, position FROM support.ticket_types WHERE tenant_id = @tenant AND is_internal ORDER BY position", connection);
            command.Parameters.AddWithValue("tenant", tenant);
            var rows = new List<(string, int)>();
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                rows.Add((reader.GetString(0), reader.GetInt32(1)));
            }

            return rows;
        }

        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
        }

        // Tenant 1: eight types and two tickets, one manual (Created event with an actor) and one from the public form (no actor).
        // Tenant 2: a type of its own called like the internal one, at position 1, and another at position 3. Tenant 3: settings only.
        private async Task SeedOldRowsAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(Ct);
            foreach (var tenant in new[] { TenantWithTickets, TenantWithItsOwnTask, TenantWithoutTypes })
            {
                await ExecuteAsync(
                    connection,
                    "INSERT INTO support.tenant_settings (tenant_id, due_soon_business_days, created_at, updated_at) VALUES (@tenant, 3, now(), now())",
                    ("tenant", tenant));
            }

            Guid? firstType = null;
            for (var position = 1; position <= 8; position++)
            {
                var id = Guid.CreateVersion7();
                firstType ??= id;
                await InsertTypeAsync(connection, id, TenantWithTickets, $"Tipo {position}", position);
            }

            await InsertTypeAsync(connection, Guid.CreateVersion7(), TenantWithItsOwnTask, "Tarea interna", 1);
            await InsertTypeAsync(connection, Guid.CreateVersion7(), TenantWithItsOwnTask, "Otro", 3);

            await InsertTicketAsync(connection, ManualTicket, TenantWithTickets, firstType!.Value, 1, "Manual", Actor);
            await InsertTicketAsync(connection, PublicTicket, TenantWithTickets, firstType.Value, 2, "PublicForm", null);
        }

        private static Task InsertTypeAsync(NpgsqlConnection connection, Guid id, Guid tenant, string name, int position) =>
            ExecuteAsync(
                connection,
                """
                INSERT INTO support.ticket_types (id, tenant_id, name, term_is_legal, requires_formal_response, is_published, is_active,
                    position, created_at, updated_at, version)
                VALUES (@id, @tenant, @name, false, false, true, true, @position, now(), now(), 1)
                """,
                ("id", id), ("tenant", tenant), ("name", name), ("position", position));

        private static async Task InsertTicketAsync(
            NpgsqlConnection connection, Guid id, Guid tenant, Guid type, long number, string intake, Guid? actor)
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO support.tickets (id, tenant_id, number, ticket_type_id, type_name, term_is_legal, requires_formal_response,
                    subject, description, intake, priority, reply_channel, status, received_at, received_on, last_event_ordinal,
                    created_at, updated_at, version)
                VALUES (@id, @tenant, @number, @type, 'Tipo 1', false, false, 'Asunto', 'Descripción', @intake, 'Medium', 'Email', 'New',
                    now(), current_date, 1, now(), now(), 1)
                """,
                ("id", id), ("tenant", tenant), ("number", number), ("type", type), ("intake", intake));
            await ExecuteAsync(
                connection,
                """
                INSERT INTO support.ticket_events (id, tenant_id, ticket_id, ordinal, kind, actor_member_id, occurred_at, details)
                VALUES (gen_random_uuid(), @tenant, @ticket, 1, 'Created', @actor, now(), '{}')
                """,
                ("tenant", tenant), ("ticket", id), ("actor", actor));
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            await command.ExecuteNonQueryAsync(Ct);
        }
    }
}
