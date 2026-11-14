using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

// Las clases comparten un servidor de Postgres (ver StartServerAsync): este tope es el que dimensiona su
// max_connections. Mismo valor que QuotationsApiHarness.
[assembly: CollectionBehavior(MaxParallelThreads = 16)]

namespace Modules.Catalog.IntegrationTests;

/// <summary>
/// Las bases de las pruebas de integración de Catalog: un solo contenedor por ensamblado, con una plantilla ya
/// migrada. Cada prueba clona la plantilla (<c>CREATE DATABASE … TEMPLATE</c>) y su host sólo comprueba que no
/// hay migraciones pendientes. El clon copia también las extensiones de la plantilla, entre ellas
/// <c>pg_trgm</c> en <c>public</c>, que es donde la busca Catalog. Copia de QuotationsApiHarness: no hay un
/// proyecto de pruebas compartido donde ponerlo.
/// </summary>
internal static class CatalogTestDatabase
{
    private const string TemplateDatabase = "qep_template_catalog";

    private static readonly Lazy<Task<PostgreSqlContainer>> SharedServer = new(StartServerAsync);

    // Postgres rechaza dos CREATE DATABASE simultáneos desde la misma plantilla: los clones van de a uno.
    private static readonly SemaphoreSlim CloneGate = new(1, 1);

    /// <summary>Una base limpia y migrada para esta prueba, clonada de la plantilla. Se borra en
    /// <c>DisposeAsync</c>.</summary>
    public static async Task<TestDatabase> StartDatabaseAsync()
    {
        var server = await SharedServer.Value;
        var name = $"t_{Guid.CreateVersion7():N}";
        await CloneGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await ExecuteAdminAsync(server, $"CREATE DATABASE \"{name}\" TEMPLATE \"{TemplateDatabase}\"");
        }
        finally
        {
            CloneGate.Release();
        }

        return new TestDatabase(server, name);
    }

    /// <summary>Una base vacía, sin migrar, en el mismo servidor. Para las pruebas de migración, que suben
    /// desde cero hasta una migración puntual (y bajan): un clon de la plantilla ya trae todo.</summary>
    public static async Task<TestDatabase> StartEmptyDatabaseAsync()
    {
        var server = await SharedServer.Value;
        var name = $"e_{Guid.CreateVersion7():N}";
        await ExecuteAdminAsync(server, $"CREATE DATABASE \"{name}\"");
        return new TestDatabase(server, name);
    }

    private static async Task<PostgreSqlContainer> StartServerAsync()
    {
        // Sin el token de una prueba: el servidor es de todas, y cancelar la primera no puede dejar a las
        // demás sin base.
        var server = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            // Las clases corren en paralelo (MaxParallelThreads, arriba), cada prueba con su host y sus pools
            // de Npgsql por módulo, más los workers: el límite por defecto (100) se agota con 53300. fsync,
            // synchronous_commit y full_page_writes apagados: la base es desechable y lo que se mide es la
            // lógica, no la durabilidad.
            .WithCommand(
                "-c", "max_connections=800",
                "-c", "fsync=off",
                "-c", "synchronous_commit=off",
                "-c", "full_page_writes=off")
            .Build();
        await server.StartAsync(CancellationToken.None);
        await ExecuteAdminAsync(server, $"CREATE DATABASE \"{TemplateDatabase}\"");

        // El host migra al arrancar: arrancarlo una vez contra la plantilla la deja con todo.
        using (var factory = new TemplateFactory(ConnectionStringFor(server, TemplateDatabase)))
        using (factory.CreateClient())
        {
        }

        // Una conexión viva a la plantilla haría fallar todos los clones.
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync(server, $"ALTER DATABASE \"{TemplateDatabase}\" WITH ALLOW_CONNECTIONS false");
        // ALLOW_CONNECTIONS no corta las sesiones abiertas, y un worker del host que se está apagando todavía puede
        // tener una: con ella, el primer clon falla con «source database is being accessed by other users».
        await ExecuteAdminAsync(
            server,
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{TemplateDatabase}' AND pid <> pg_backend_pid()");
        return server;
    }

    private static string ConnectionStringFor(PostgreSqlContainer server, string database) =>
        new NpgsqlConnectionStringBuilder(server.GetConnectionString()) { Database = database }.ConnectionString;

    private static async Task ExecuteAdminAsync(PostgreSqlContainer server, string sql)
    {
        await using var connection = new NpgsqlConnection(server.GetConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    /// <summary>La base de una prueba en el servidor compartido.</summary>
    public sealed class TestDatabase(PostgreSqlContainer server, string name) : IAsyncDisposable
    {
        public string GetConnectionString() => ConnectionStringFor(server, name);

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearPool(new NpgsqlConnection(GetConnectionString()));
            await ExecuteAdminAsync(server, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
    }

    /// <summary>El host que migra la plantilla. Misma configuración base que los hosts de las pruebas, y la
    /// semilla apagada a la fuerza: la plantilla tiene que quedar con el esquema y nada más, aunque los
    /// user-secrets de quien corre las pruebas la prendan.</summary>
    private sealed class TemplateFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Seed:Enabled", "false");
        }
    }
}
