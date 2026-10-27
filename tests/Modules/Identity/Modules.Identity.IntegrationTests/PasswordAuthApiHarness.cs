using Microsoft.EntityFrameworkCore;
using Modules.Identity.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Helpers compartidos por las pruebas de registro y login con contraseña. Se importan con
/// <c>using static</c>. Cada ensamblado de pruebas tiene su propia copia: no hay proyecto compartido.
/// </summary>
internal static class PasswordAuthApiHarness
{
    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    // Mismas opciones que IdentityDbContextFactory: el historial de migraciones vive en el
    // esquema identity.
    public static IdentityDbContext NewIdentityContext(string connectionString) =>
        new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "identity"))
            .Options);

    public static async Task<DateTimeOffset?> EmailVerifiedAtAsync(string connectionString, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT email_verified_at FROM identity.users WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? null : new DateTimeOffset((DateTime)value, TimeSpan.Zero);
    }

    // `table` es siempre una constante de la prueba, nunca entrada externa.
    public static async Task<long> CountRowsAsync(string connectionString, string table, Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT COUNT(*) FROM {table} WHERE user_id = @id", connection);
        command.Parameters.AddWithValue("id", userId);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
