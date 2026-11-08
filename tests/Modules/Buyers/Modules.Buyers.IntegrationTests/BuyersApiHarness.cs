using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Buyers.Application;
using Modules.Buyers.Domain;
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
        var captcha = new FakeBuyerCaptcha();
        var factory = new QepApiFactory(
            database.GetConnectionString(),
            services =>
            {
                // Nunca salen a Cloudflare ni resuelven una tienda real; lo que una prueba pida después manda.
                services.RemoveAll<IBuyerStorefronts>();
                services.AddSingleton<IBuyerStorefronts>(new FakeBuyerStorefronts());
                services.RemoveAll<IBuyerCaptcha>();
                services.AddSingleton<IBuyerCaptcha>(captcha);
                configure?.Invoke(services);
            },
            runBackgroundServices);
        return new BuyersApi(database, factory, captcha);
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

    internal sealed class BuyersApi(PostgreSqlContainer database, QepApiFactory factory, FakeBuyerCaptcha captcha)
        : IAsyncDisposable
    {
        /// <summary>Pide la IP única de la petición (la del harness); es el valor por defecto de <c>PostAsync</c>.</summary>
        public const string AutoIp = "auto";

        public QepApiFactory Factory { get; } = factory;

        public FakeBuyerCaptcha Captcha { get; } = captcha;

        /// <summary>El cliente del storefront: lleva la clave y una IP de visitante distinta en cada petición.</summary>
        public HttpClient Storefront { get; } = CreateStorefront(factory, fixedIp: null, uniqueIp: true);

        // Lleva la clave pero nunca una IP: para probar la ruta sin el header del visitante.
        private HttpClient WithoutVisitorIp { get; } = CreateStorefront(factory, fixedIp: null, uniqueIp: false);

        /// <summary>
        /// Un POST a <c>/buyer{path}</c> como lo haría el BFF. <paramref name="visitorIp"/>: <see cref="AutoIp"/> = una IP única;
        /// <c>null</c> = sin el header; cualquier otro valor se manda tal cual (así una prueba fija una IP para el límite).
        /// </summary>
        public Task<HttpResponseMessage> PostAsync(
            string path, object body, string host = "acme.lulocrm.com", string? session = null, string? visitorIp = AutoIp)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/public/stores/{host}/buyer{path}")
            {
                Content = JsonContent.Create(body),
            };
            if (session is not null)
            {
                request.Headers.Add("X-Qep-Buyer-Session", session);
            }

            if (visitorIp is not null && visitorIp != AutoIp)
            {
                request.Headers.Add("X-Qep-Visitor-Ip", visitorIp);
            }

            var client = visitorIp is null ? WithoutVisitorIp : Storefront;
            return client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        /// <summary>Un PUT a <c>/buyer{path}</c> con cuerpo JSON y la sesión opcional, como lo haría el BFF.</summary>
        public Task<HttpResponseMessage> PutAsync(
            string path, object body, string? session = null, string host = "acme.lulocrm.com")
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/public/stores/{host}/buyer{path}")
            {
                Content = JsonContent.Create(body),
            };
            if (session is not null)
            {
                request.Headers.Add("X-Qep-Buyer-Session", session);
            }

            return Storefront.SendAsync(request, TestContext.Current.CancellationToken);
        }

        /// <summary>Un GET o DELETE a <c>/buyer{path}</c> con la sesión opcional, como lo haría el BFF.</summary>
        public Task<HttpResponseMessage> SendAsync(
            HttpMethod method, string path, string? session = null, string host = "acme.lulocrm.com")
        {
            var request = new HttpRequestMessage(method, $"/api/v1/public/stores/{host}/buyer{path}");
            if (session is not null)
            {
                request.Headers.Add("X-Qep-Buyer-Session", session);
            }

            return Storefront.SendAsync(request, TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Deja una cuenta verificada, por las rutas reales: registra y consume el token que el outbox lleva. Lo reutilizan las
        /// pruebas que necesitan una cuenta lista para ingresar. Devuelve la contraseña usada.
        /// </summary>
        public async Task<string> RegisterVerifiedAsync(
            string email = "ana@correo.com", string password = "una-contrasena-larga", string host = "acme.lulocrm.com")
        {
            var registered = await PostAsync(
                "/register",
                new { email, password, acceptsDataPolicy = true, turnstileToken = "token-ok", profile = (object?)null },
                host);
            registered.EnsureSuccessStatusCode();
            var normalized = email.Trim().ToLowerInvariant();
            var token = (await OutboxPayloadsAsync("buyers.email-verification-requested.v1"))
                .Last(payload => (string?)payload["email"] == normalized)["token"]!.GetValue<string>();
            var verified = await PostAsync("/email/verify", new { token }, host);
            verified.EnsureSuccessStatusCode();
            return password;
        }

        /// <summary>Un cliente del storefront con una IP de visitante fija, para probar el límite por IP.</summary>
        public HttpClient StorefrontWithIp(string visitorIp) => CreateStorefront(Factory, visitorIp, uniqueIp: false);

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
            WithoutVisitorIp.Dispose();
            await Factory.DisposeAsync();
            await database.DisposeAsync();
        }

        private async Task<NpgsqlConnection> OpenConnectionAsync()
        {
            var connection = new NpgsqlConnection(database.GetConnectionString());
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            return connection;
        }

        private static HttpClient CreateStorefront(QepApiFactory factory, string? fixedIp, bool uniqueIp)
        {
            var client = uniqueIp
                ? factory.CreateDefaultClient(new UniqueVisitorIpHandler())
                : factory.CreateDefaultClient();
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

/// <summary>
/// El puerto de la tienda sin Store de por medio: cualquier host es una tienda llamada "Tienda Demo" (el host sale normalizado),
/// salvo <c>no-existe.lulocrm.com</c>, que lanza el mismo 404 que el resolvedor real.
/// </summary>
internal sealed class FakeBuyerStorefronts : IBuyerStorefronts
{
    public const string MissingHost = "no-existe.lulocrm.com";

    public Task<BuyerStorefront> ResolveAsync(string host, CancellationToken cancellationToken) =>
        string.Equals(host.Trim(), MissingHost, StringComparison.OrdinalIgnoreCase)
            ? throw new ResourceNotFoundException(BuyerErrorCodes.StoreNotFound, "The store was not found.")
            : Task.FromResult(new BuyerStorefront(host.Trim().ToLowerInvariant(), "Tienda Demo"));
}

/// <summary>El desafío controlable por prueba: nunca sale a Cloudflare.</summary>
internal sealed class FakeBuyerCaptcha : IBuyerCaptcha
{
    private int _calls;

    public bool Passes { get; set; } = true;

    public int Calls => _calls;

    public Task<bool> VerifyAsync(string token, string visitorIp, string host, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(Passes);
    }
}
