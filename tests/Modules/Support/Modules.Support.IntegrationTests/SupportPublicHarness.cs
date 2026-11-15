using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Support.Application;
using Modules.Support.Domain;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Testcontainers.PostgreSql;
using static Modules.Support.IntegrationTests.SupportApiHarness;
using static Modules.Support.IntegrationTests.SupportMessagingSeed;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// Lo que comparten las pruebas del formulario público (plan fase 3, Bloque B): la siembra de un tenant con el formulario publicado,
/// las rutas del grupo anónimo y un cliente HTTP que llega «desde» una IP de visitante. La IP se fija como en
/// <c>ForwardedHeadersApiTests</c>: el par directo es un nodo del ingress de confianza y la del visitante viaja en <c>X-Real-IP</c>,
/// así cada prueba tiene su propia cubeta en los limitadores aunque compartan host.
/// </summary>
internal static class SupportPublicHarness
{
    public const string SupportOrigin = "https://soporte.example.com";

    public const string ConsoleOrigin = "https://console.example.com";

    private const string TrustedNetwork = "10.50.0.0/24";

    private static readonly IPAddress IngressNode = IPAddress.Parse("10.50.0.21");

    private static int _nextIp;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string PublicUrl(string slug, string path = "") => $"/api/v1/public/support/{slug}{path}";

    /// <summary>Una IP de visitante distinta en cada llamada (198.18.0.0/15, reservada para pruebas).</summary>
    public static string NextIp()
    {
        var next = Interlocked.Increment(ref _nextIp);
        return $"198.18.{next / 250}.{(next % 250) + 1}";
    }

    /// <summary>Un cliente cuyo par directo es el ingress de confianza y cuya IP de visitante es <paramref name="visitorIp"/>.</summary>
    public static HttpClient VisitorClient(QepApiFactory factory, string? visitorIp = null)
    {
        var client = new HttpClient(factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = IngressNode))
        {
            BaseAddress = factory.Server.BaseAddress,
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Real-IP", visitorIp ?? NextIp());
        return client;
    }

    /// <summary>La configuración de los hosts públicos: orígenes de la página y de la consola, y el ingress de confianza.</summary>
    public static Dictionary<string, string> Settings(bool realAuthentication)
    {
        var settings = new Dictionary<string, string>
        {
            ["Support:PublicForm:AllowedOrigins:0"] = SupportOrigin,
            ["Cors:AllowedOrigins:0"] = ConsoleOrigin,
            ["ForwardedHeaders:KnownNetworks:0"] = TrustedNetwork,
            // El seguimiento del hilo lee toda la base cada 5 s; estas pruebas no lo necesitan.
            ["Support:Workers:Enabled"] = "false",
        };
        if (realAuthentication)
        {
            // Precedente ShopifyApiHarness.WithRealAuthentication: sin el stub se registra la defensa CSRF y la cookie se valida de
            // verdad. Ningún request público se autentica.
            settings["Authentication:UseDevelopmentStub"] = "false";
            settings["Authentication:Audience"] = "qep-support-public-tests";
        }

        return settings;
    }

    public static async Task<string> SlugOfAsync(QepApiFactory factory, string tenantId) =>
        (await TextAsync(factory, "SELECT slug FROM tenancy.tenants WHERE id = @id", ("id", Guid.Parse(tenantId))))!;

    /// <summary>
    /// Un tenant con el formulario publicado: su fila y sus módulos, los tipos por defecto (todos publicados), la versión 1 de los
    /// textos legales y la versión 1 del formulario. Por SQL de dominio y no por la consola: el host puede tener la autenticación real.
    /// Devuelve el slug.
    /// </summary>
    public static async Task<string> SeedPublishedTenantAsync(
        QepApiFactory factory,
        string tenantId,
        IEnumerable<TenantModuleKey>? modules = null,
        bool published = true,
        bool showDocument = true,
        bool showAddress = true,
        bool showLandline = true,
        bool businessNameRequired = true)
    {
        await SeedTenantAsync(factory, tenantId, modules);
        var tenant = Guid.Parse(tenantId);
        var now = DateTimeOffset.UtcNow;
        await using (var provision = factory.Services.CreateAsyncScope())
        {
            await provision.ServiceProvider.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenant, Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportLegalTextRepository>().Add(SupportLegalTextVersion.Create(
                Guid.CreateVersion7(), tenant, 1, "Autorizo el tratamiento de mis datos personales, versión 1.", null,
                "https://example.com/politica", "Términos, versión 1.", Guid.CreateVersion7(), now));
            scope.ServiceProvider.GetRequiredService<ISupportFormRepository>().Add(SupportFormVersion.Create(
                Guid.CreateVersion7(), tenant, 1, published, showDocument, showAddress, showLandline, businessNameRequired,
                "#00B4FF", "Cuéntanos tu caso.", "Recibimos tu caso.", Guid.CreateVersion7(), now));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        return await SlugOfAsync(factory, tenantId);
    }

    /// <summary>Publica una versión nueva de los textos legales, como si el tenant los editara.</summary>
    public static async Task PublishLegalTextsAsync(QepApiFactory factory, string tenantId, int version, string authorizationText)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ISupportLegalTextRepository>().Add(SupportLegalTextVersion.Create(
            Guid.CreateVersion7(), Guid.Parse(tenantId), version, authorizationText, null, "https://example.com/politica", null,
            Guid.CreateVersion7(), DateTimeOffset.UtcNow));
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    public static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Ct);

    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}

/// <summary>Turnstile de mentira: pasa sólo con <see cref="ValidToken"/>. Anota cada verificación con la IP que recibió.</summary>
internal sealed class FakeSupportCaptcha : ISupportCaptcha
{
    public const string ValidToken = "turnstile-ok";

    public ConcurrentQueue<(string Token, string VisitorIp)> Calls { get; } = new();

    public Task<bool> VerifyAsync(string token, string visitorIp, CancellationToken cancellationToken)
    {
        Calls.Enqueue((token, visitorIp));
        return Task.FromResult(string.Equals(token, ValidToken, StringComparison.Ordinal));
    }
}

/// <summary>
/// Un host propio por clase para las pruebas del formulario público: Turnstile de mentira, el reloj del tenant movible y los orígenes
/// CORS de prueba. No es <see cref="SharedSupportHost"/>: cambia servicios globales del host. Cada prueba siembra su tenant y llega
/// desde su propia IP (<see cref="SupportPublicHarness.VisitorClient"/>), así los limitadores no se cruzan entre pruebas.
/// <see cref="StartTestAsync"/> devuelve el reloj a <see cref="ClockedSupportHost.Reception"/>.
/// </summary>
public class SupportPublicHost : IAsyncLifetime
{
    private PostgreSqlContainer? _database;
    private QepApiFactory? _factory;

    internal FakeSupportCaptcha Captcha { get; } = new();

    internal MutableTenantClock Clock { get; } = new(ClockedSupportHost.Reception);

    /// <summary>Con la autenticación real (sin el stub): la defensa CSRF corre y la cookie de sesión se valida de verdad.</summary>
    protected virtual bool RealAuthentication => false;

    internal Task<QepApiFactory> StartTestAsync()
    {
        Clock.UtcNow = ClockedSupportHost.Reception;
        return Task.FromResult(_factory ?? throw new InvalidOperationException("The public host has not been initialized."));
    }

    /// <summary>Para sumar dobles en una clase derivada.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    public async ValueTask InitializeAsync()
    {
        _database = await StartDatabaseAsync();
        _factory = new QepApiFactory(
            _database.GetConnectionString(),
            services =>
            {
                RemoveThreadFollowWorker(services);
                services.RemoveAll<ISupportCaptcha>();
                services.AddSingleton<ISupportCaptcha>(Captcha);
                services.RemoveAll<ITenantClock>();
                services.AddSingleton<ITenantClock>(Clock);
                ConfigureServices(services);
            },
            SupportPublicHarness.Settings(RealAuthentication));
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary><see cref="SupportPublicHost"/> con la autenticación real.</summary>
public sealed class SupportPublicRealAuthHost : SupportPublicHost
{
    protected override bool RealAuthentication => true;
}
