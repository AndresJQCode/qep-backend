using Npgsql;

namespace Modules.Quotations.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas de WhatsApp por tenant (spec 2026-10-07). Aparte de
/// <see cref="QuotationsApiHarness"/> porque nada de esto lo usa otra suite.
/// </summary>
internal static class WhatsAppTestHarness
{
    /// <summary>Una key de Zenvia inventada y fácil de buscar: la prueba de fugas la persigue por
    /// respuestas, logs y <c>platform.request_failures</c>. ASCII visible, sin espacios: pasa el
    /// validador.</summary>
    public const string SentinelApiKey = "zenvia-key-SENTINEL-7f3a9c";

    public const string FromNumber = "573001234567";

    public const string TemplateId = "9b2f4c1e-3d5a-4e6b-8c7d-1a2b3c4d5e6f";

    public static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public static async Task<int> ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
