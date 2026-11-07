using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Buyers.IntegrationTests;

/// <summary>
/// Arranque compartido de las pruebas de integración de Buyers. Copia el de <c>StoreApiHarness</c>: una base
/// por prueba, la fábrica con las mismas claves y el cliente del storefront con su clave.
/// </summary>
internal static class BuyersApiHarness
{
    public const string StorefrontKey = "integration-tests-storefront-secret-0123456789";

    // Un contador por proceso: cada petición recibe su propia IP, así el límite de 10 por minuto por ruta
    // nunca produce un 429 intermitente entre pruebas.
    private static int _ipCounter;

    public static async Task<BuyersApi> OpenAsync(
        Action<IServiceCollection>? configure = null, bool runBackgroundServices = false)
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        var factory = new QepApiFactory(database.GetConnectionString(), configure, runBackgroundServices);
        return new BuyersApi(database, factory);
    }

    /// <summary>Una IP 10.x.y.z distinta en cada llamada.</summary>
    public static string NextVisitorIp()
    {
        var value = Interlocked.Increment(ref _ipCounter);
        return $"10.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
    }

    /// <summary>Pone una IP única si la petición no trae ya la suya; la que fija una prueba manda.</summary>
    private sealed class UniqueVisitorIpHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains("X-Qep-Visitor-Ip"))
            {
                request.Headers.Add("X-Qep-Visitor-Ip", NextVisitorIp());
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    internal sealed class BuyersApi(PostgreSqlContainer database, QepApiFactory factory) : IAsyncDisposable
    {
        public QepApiFactory Factory { get; } = factory;

        /// <summary>El cliente del storefront: lleva la clave y una IP de visitante distinta en cada petición.</summary>
        public HttpClient Storefront { get; } = CreateStorefront(factory, fixedIp: null);

        /// <summary>Un cliente del storefront con una IP de visitante fija, para probar el límite por IP.</summary>
        public HttpClient StorefrontWithIp(string visitorIp) => CreateStorefront(Factory, visitorIp);

        public async Task<IReadOnlyList<JsonNode>> OutboxPayloadsAsync(string eventName)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT payload::text FROM platform.outbox_messages WHERE event_name = $1 ORDER BY occurred_at, id",
                connection);
            command.Parameters.Add(new NpgsqlParameter { Value = eventName });
            var payloads = new List<JsonNode>();
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                payloads.Add(JsonNode.Parse(reader.GetString(0))!);
            }

            return payloads;
        }

        public async Task<T> ScalarAsync<T>(string sql, params object[] args)
        {
            await using var connection = await OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var arg in args)
            {
                command.Parameters.Add(new NpgsqlParameter { Value = arg });
            }

            var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            return result is null or DBNull
                ? default!
                : result is T typed ? typed : (T)Convert.ChangeType(result, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
        }

        public T Scoped<T>(Func<IServiceProvider, T> use)
        {
            using var scope = Factory.Services.CreateScope();
            return use(scope.ServiceProvider);
        }

        public async ValueTask DisposeAsync()
        {
            Storefront.Dispose();
            await Factory.DisposeAsync();
            await database.DisposeAsync();
        }

        private async Task<NpgsqlConnection> OpenConnectionAsync()
        {
            var connection = new NpgsqlConnection(database.GetConnectionString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            return connection;
        }

        private static HttpClient CreateStorefront(QepApiFactory factory, string? fixedIp)
        {
            var client = factory.CreateDefaultClient(new UniqueVisitorIpHandler());
            client.DefaultRequestHeaders.Add("X-Qep-Storefront-Key", StorefrontKey);
            if (fixedIp is not null)
            {
                client.DefaultRequestHeaders.Add("X-Qep-Visitor-Ip", fixedIp);
            }

            return client;
        }
    }

    /// <param name="configureServices">Sustituciones de servicios; corre después del registro de la aplicación.</param>
    /// <param name="runBackgroundServices">
    /// Los servicios en segundo plano de Buyers quedan apagados: sondean por su cuenta y volverían no
    /// determinista una prueba que drena a mano. Mismo interruptor que <c>runDeliveryWorkers</c> de Notifications.
    /// </param>
    internal sealed class QepApiFactory(
        string connectionString,
        Action<IServiceCollection>? configureServices = null,
        bool runBackgroundServices = false)
        : WebApplicationFactory<Program>
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes el validador de
            // Notifications falla al arrancar.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true.
            builder.UseSetting("Seed:Enabled", "false");
            // Valores de juguete (nunca reales).
            builder.UseSetting("Store:Storefront:SharedSecret", StorefrontKey);
            builder.UseSetting("Store:Turnstile:SecretKey", "integration-tests-turnstile-secret");

            builder.ConfigureServices(services =>
            {
                if (!runBackgroundServices)
                {
                    var workers = services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                            && descriptor.ImplementationType?.Namespace?.StartsWith(
                                "Modules.Buyers", StringComparison.Ordinal) == true)
                        .ToList();
                    foreach (var descriptor in workers)
                    {
                        services.Remove(descriptor);
                    }
                }

                configureServices?.Invoke(services);
            });
        }
    }
}
