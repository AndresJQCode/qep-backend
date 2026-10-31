using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// El endpoint que recibe los tiempos que mide la SPA (navegación y Web Vitals) y los publica como métricas del
/// meter <c>Qep.Web</c>. Sin base ni dispatcher: lo que se prueba es la validación, el aislamiento por tenant, la
/// etiqueta de ruta acotada y el límite por IP.
/// </summary>
public sealed class WebTelemetryApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    private static string Url => $"/api/v1/tenants/{TenantId}/telemetry/web-vitals";

    private sealed record Measurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

    private sealed record ProblemPayload(string? Code, Dictionary<string, string[]>? Errors);

    /// <summary>Las mediciones del host de la prueba: el filtro por <c>Meter.Scope</c> evita que las pruebas en paralelo se crucen.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Measurement> _measurements = [];

        public Recorder(IMeterFactory factory)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Qep.Web" && ReferenceEquals(instrument.Meter.Scope, factory))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            {
                var copy = new Dictionary<string, object?>();
                foreach (var tag in tags)
                {
                    copy[tag.Key] = tag.Value;
                }

                lock (_measurements)
                {
                    _measurements.Add(new Measurement(instrument.Name, value, copy));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Measurements
        {
            get
            {
                lock (_measurements)
                {
                    return [.. _measurements];
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static string Batch(params (string Metric, string? Route, double Value)[] samples) =>
        JsonSerializer.Serialize(new
        {
            samples = samples.Select(sample => new { metric = sample.Metric, route = sample.Route, value = sample.Value }),
        });

    [Fact]
    public async Task ABatchIsAcceptedAndRecorded()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var recorder = new Recorder(factory.Services.GetRequiredService<IMeterFactory>());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await client.PostAsync(
            Url,
            Json(Batch(
                ("navigation", "/quotations/$id", 1.25),
                ("lcp", "/quotations/$id", 2.1),
                ("cls", null, 0.05))),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var measurements = recorder.Measurements;
        var navigation = Assert.Single(measurements, m => m.Instrument == "qep.web.navigation.duration");
        Assert.Equal(1.25, navigation.Value);
        Assert.Equal("/quotations/$id", navigation.Tags["route"]);
        // Sólo la navegación lleva ruta: una etiqueta de ruta en los vitales multiplicaría las series sin que nadie las lea.
        var vital = Assert.Single(measurements, m => m.Instrument == "qep.web.vital");
        Assert.Equal(2.1, vital.Value);
        Assert.Equal("lcp", vital.Tags["name"]);
        Assert.False(vital.Tags.ContainsKey("route"));
        var cls = Assert.Single(measurements, m => m.Instrument == "qep.web.cls");
        Assert.Equal(0.05, cls.Value);
        Assert.Empty(cls.Tags);
    }

    [Fact]
    public async Task AnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await client.PostAsync(
            Url, Json(Batch(("lcp", null, 1))), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithoutSessionIsUnauthorized()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            Url, Json(Batch(("lcp", null, 1))), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Con el stub de auth el middleware de CSRF ni se registra: sólo se ve con la autenticación real.</summary>
    [Fact]
    public async Task WithoutClientHeaderIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString(), realAuthentication: true);
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var response = await client.PostAsync(
            Url, Json(Batch(("lcp", null, 1))), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public static TheoryData<string> InvalidBatches()
    {
        var fiftyOne = JsonSerializer.Serialize(new
        {
            samples = Enumerable.Range(0, 51).Select(_ => new { metric = "lcp", value = 1.0 }),
        });
        return new TheoryData<string>
        {
            fiftyOne,
            Batch(("lcp", null, -1)),
            Batch(("lcp", null, 121)),
            Batch(("foo", null, 1)),
            Batch(("navigation", null, 1)),
            Batch(("navigation", "bad route", 1)),
            // Un salto de línea final: `$` lo aceptaría, `\z` no.
            Batch(("navigation", "/quotations/$id\n", 1)),
            "{}",
            "{",
            """{"samples":"x"}""",
            """{"samples":[{"metric":"lcp","value":"a"}]}""",
            """{"samples":[null]}""",
            // 17 KB: pasa del tope del cuerpo aunque sea JSON legal.
            $$"""{"samples":[],"pad":"{{new string('a', 17 * 1024)}}"}""",
        };
    }

    [Theory]
    [MemberData(nameof(InvalidBatches))]
    public async Task AnInvalidBatchIs422WithErrors(string body)
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await client.PostAsync(Url, Json(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            JsonSerializerOptions.Web, TestContext.Current.CancellationToken);
        Assert.Equal("validation.failed", problem?.Code);
        Assert.NotEmpty(problem?.Errors ?? []);
    }

    /// <summary>El tenant se comprueba antes de leer el cuerpo: un cuerpo roto de otro tenant es 403, no 422.</summary>
    [Fact]
    public async Task AnotherTenantWithAMalformedBodyIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await client.PostAsync(Url, Json("{"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>El lote legal más grande (50 muestras con rutas de 160 caracteres) cabe en el tope de 16 KB.</summary>
    [Fact]
    public async Task TheLargestLegalBatchFitsTheBodyLimit()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var samples = Enumerable.Range(0, 50)
            .Select(i => ("navigation", (string?)("/" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + new string('a', 156)), 120.0))
            .ToArray();
        var body = Batch(samples);
        Assert.True(body.Length < 16 * 1024, $"body was {body.Length}");

        var response = await client.PostAsync(Url, Json(body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>La etiqueta de ruta tiene tope: la ruta 257 sale como <c>other</c>, una ya vista sigue por su nombre.</summary>
    [Fact]
    public async Task TheRoute257IsOther()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var recorder = new Recorder(factory.Services.GetRequiredService<IMeterFactory>());
        using var client = CreateClient(factory, SubjectId, TenantId);

        for (var start = 0; start < 256; start += 32)
        {
            var batch = Enumerable.Range(start, 32).Select(i => ("navigation", (string?)$"/r{i}", 1.0)).ToArray();
            var ok = await client.PostAsync(Url, Json(Batch(batch)), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        }

        var routes = recorder.Measurements.Select(m => (string)m.Tags["route"]!).ToArray();
        Assert.Equal(256, routes.Length);
        Assert.Equal(Enumerable.Range(0, 256).Select(i => $"/r{i}"), routes);

        var last = await client.PostAsync(
            Url,
            Json(Batch(("navigation", "/r256", 1), ("navigation", "/r7", 1))),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, last.StatusCode);
        var tail = recorder.Measurements.Skip(256).Select(m => (string)m.Tags["route"]!).ToArray();
        Assert.Equal(["other", "/r7"], tail);
    }

    [Fact]
    public async Task TheTelemetryPolicyLimitsByIp()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        HttpStatusCode last = default;
        var accepted = 0;
        for (var i = 0; i < 601; i++)
        {
            using var content = Json(Batch(("lcp", null, 1)));
            using var response = await client.PostAsync(Url, content, TestContext.Current.CancellationToken);
            last = response.StatusCode;
            if (last == HttpStatusCode.NoContent)
            {
                accepted++;
            }
        }

        Assert.Equal(600, accepted);
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    private static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(TestContext.Current.CancellationToken);
        return database;
    }

    private static HttpClient CreateClient(QepApiFactory factory, string subjectId, string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    private sealed class QepApiFactory(string connectionString, bool realAuthentication = false)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // "IntegrationTests" deja la autenticación real (ver RealAuthenticationApiTests); "Development" trae el stub.
            builder.UseEnvironment(realAuthentication ? "IntegrationTests" : "Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, no heredado: ver AuthorizationCatalogApiTests.QepApiFactory.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            if (realAuthentication)
            {
                builder.UseSetting("Authentication:Audience", "test-audience");
                builder.UseSetting(
                    "Authentication:TestSigningKey",
                    Convert.ToBase64String(new byte[32]));
                builder.UseSetting("Platform:OperatorTenantId", string.Empty);
            }
        }
    }
}
