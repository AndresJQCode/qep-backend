using Microsoft.EntityFrameworkCore;
using Modules.Scheduling.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// Una base vacía y migrada sólo con el esquema de la Agenda, en un Postgres compartido por el ensamblado. Las pruebas de persistencia
/// (aprovisionador, restricción de exclusión, mapeos, migración) no necesitan el host entero; las de la API usan su propio harness.
/// </summary>
internal sealed class SchedulingDatabase : IAsyncDisposable
{
    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    private readonly PostgreSqlContainer _server;
    private readonly string _name;

    private SchedulingDatabase(PostgreSqlContainer server, string name)
    {
        _server = server;
        _name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(server.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }

    public string ConnectionString { get; }

    /// <summary>Crea una base vacía en el servidor compartido y aplica las migraciones de la Agenda.</summary>
    public static async Task<SchedulingDatabase> StartAsync(bool migrate = true)
    {
        var server = await SharedServer.Value;
        var name = $"s_{Guid.CreateVersion7():N}";
        await ExecuteAdminAsync(server, $"CREATE DATABASE \"{name}\"");
        var database = new SchedulingDatabase(server, name);
        if (migrate)
        {
            await using var context = database.CreateContext();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        }

        return database;
    }

    public SchedulingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", SchedulingDbContext.Schema))
            .Options);

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync(_server, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
    }

    private static async Task<PostgreSqlContainer> StartServerAsync()
    {
        var server = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .WithCommand("-c", "max_connections=400", "-c", "fsync=off", "-c", "synchronous_commit=off", "-c", "full_page_writes=off")
            .Build();
        await server.StartAsync(CancellationToken.None);
        return server;
    }

    private static async Task ExecuteAdminAsync(PostgreSqlContainer server, string sql)
    {
        await using var connection = new NpgsqlConnection(server.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
