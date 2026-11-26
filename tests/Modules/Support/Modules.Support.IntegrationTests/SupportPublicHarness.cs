using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Modules.Companies.Application;
using Modules.Companies.Domain;
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
    /// <summary>
    /// Puts the tenant's internal ticket type in an otherwise-visible state (published and active). The CHECK
    /// <c>CK_ticket_types_internal</c> is the first line of defense and forbids exactly that; the explicit <c>IsInternal: false</c> filter of
    /// the public paths is the second, and these tests pin the second. The host database is shared by the whole class, so the caller must
    /// restore the constraint in a <c>finally</c> with <see cref="RestoreInternalTypeConstraintAsync"/>.
    /// </summary>
    public static async Task MakeInternalTypeVisibleAsync(QepApiFactory factory, Guid tenantId)
    {
        await SupportMessagingSeed.ExecuteAsync(factory, "ALTER TABLE support.ticket_types DROP CONSTRAINT IF EXISTS \"CK_ticket_types_internal\"");
        await SupportMessagingSeed.ExecuteAsync(
            factory, "UPDATE support.ticket_types SET is_published = true, is_active = true WHERE tenant_id = @tenant AND is_internal",
            ("tenant", tenantId));
    }

    /// <summary>Undoes <see cref="MakeInternalTypeVisibleAsync"/>: the flags go back to what the CHECK allows and the CHECK is re-added with
    /// the exact definition of the migration 20261122183828_AddSupportInternalTasks.</summary>
    public static async Task RestoreInternalTypeConstraintAsync(QepApiFactory factory)
    {
        await SupportMessagingSeed.ExecuteAsync(
            factory, "UPDATE support.ticket_types SET is_published = false, is_active = true WHERE is_internal");
        await SupportMessagingSeed.ExecuteAsync(
            factory,
            """
            ALTER TABLE support.ticket_types ADD CONSTRAINT "CK_ticket_types_internal" CHECK (NOT is_internal OR (term_business_days IS NULL AND NOT term_is_legal AND NOT requires_formal_response AND NOT is_published AND is_active))
            """);
    }

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
    /// tres documentos legales (spec 2026-11-15) y la versión 1 del formulario. Por SQL de dominio y no por la consola: el host puede tener la autenticación real.
    /// Con adjuntos salvo <paramref name="allowAttachments"/> en false, como el formulario por defecto (owner, 2026-11-15, E2). Con una
    /// empresa activa del tenant elegida salvo <paramref name="withOrganization"/> en false (spec 2026-11-15 §15 D-8), con el nombre
    /// <paramref name="organizationName"/> (las variables de los documentos legales la nombran, spec 2026-11-16). Devuelve el slug.
    /// </summary>
    public static async Task<string> SeedPublishedTenantAsync(
        QepApiFactory factory,
        string tenantId,
        IEnumerable<TenantModuleKey>? modules = null,
        bool published = true,
        bool showDocument = true,
        bool showAddress = true,
        bool showLandline = true,
        bool businessNameRequired = true,
        bool allowAttachments = true,
        bool withOrganization = true,
        string organizationName = "Verde S.A.S.")
    {
        await SeedTenantAsync(factory, tenantId, modules);
        // Spec 2026-11-15 §15 D-8: a form published from the console always has its company; the seed chooses one too.
        Guid? organization = withOrganization ? await SeedCompanyAsync(factory, tenantId, organizationName) : null;
        var tenant = Guid.Parse(tenantId);
        var now = DateTimeOffset.UtcNow;
        await using (var provision = factory.Services.CreateAsyncScope())
        {
            await provision.ServiceProvider.GetRequiredService<ISupportProvisioner>().EnsureAsync(tenant, Ct);
        }

        foreach (var kind in SupportLegalDocumentKinds.All)
        {
            await PublishLegalDocumentAsync(factory, tenantId, kind, 1, $"<p>{kind}, versión 1.</p>");
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ISupportFormRepository>().Add(SupportFormVersion.Create(
                Guid.CreateVersion7(), tenant, 1, published, showDocument, showAddress, showLandline, businessNameRequired,
                allowAttachments, "#00B4FF", "Cuéntanos tu caso.", "Recibimos tu caso.", Guid.CreateVersion7(), now, organization));
            await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        }

        return await SlugOfAsync(factory, tenantId);
    }

    /// <summary>
    /// A Colombian company of the tenant with NIT, created through the Companies domain and repository (not its API: the host may have
    /// real authentication). <paramref name="active"/> false leaves it deactivated. Spec 2026-11-15 §9.3: the public form shows the
    /// one it chose, and D-8 makes it required to publish.
    /// </summary>
    public static async Task<Guid> SeedCompanyAsync(
        QepApiFactory factory, string tenantId, string name = "Verde S.A.S.", string taxId = "900123456", bool active = true)
    {
        var cityId = await AnyCityIdAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var now = DateTimeOffset.UtcNow;
        var company = Company.Create(
            CompanyId.New(),
            Guid.Parse(tenantId),
            name,
            // Cut to CompanyBankAccount's 32-character limit: "CTA-" plus a whole GUID is 36.
            [new CompanyBankAccount { BankName = "Bancolombia", AccountNumber = $"CTA-{Guid.CreateVersion7():N}"[..32], Currency = "COP" }],
            taxId,
            Company.ColombiaCountryCode,
            CompanyDocumentType.Nit,
            cityId,
            null,
            CompanyContactInfo.Empty,
            now);
        if (!active)
        {
            company.Deactivate(now);
        }

        scope.ServiceProvider.GetRequiredService<ICompanyRepository>().Add(company);
        await scope.ServiceProvider.GetRequiredService<ICompaniesUnitOfWork>().SaveChangesAsync(Ct);
        return company.Id.Value;
    }

    /// <summary>Deactivates a company of the tenant, as Companies would.</summary>
    public static async Task DeactivateCompanyAsync(QepApiFactory factory, string tenantId, Guid companyId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var company = await scope.ServiceProvider.GetRequiredService<ICompanyRepository>()
            .FindAsync(Guid.Parse(tenantId), new CompanyId(companyId), Ct);
        company!.Deactivate(DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<ICompaniesUnitOfWork>().SaveChangesAsync(Ct);
    }

    /// <summary>Publica una versión nueva de un documento legal, como si el tenant la publicara desde la consola. El HTML pasa por el
    /// saneador real, como en <c>PublishLegalDocumentHandler</c>. La pregunta y la URL, sólo con el aviso de privacidad.</summary>
    public static async Task PublishLegalDocumentAsync(
        QepApiFactory factory,
        string tenantId,
        SupportLegalDocumentKind kind,
        int version,
        string html,
        string? authorizationQuestion = null,
        string? policyUrl = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var content = scope.ServiceProvider.GetRequiredService<ISupportHtmlSanitizer>().Sanitize(html);
        scope.ServiceProvider.GetRequiredService<ISupportLegalDocumentRepository>().Add(SupportLegalDocumentVersion.Create(
            Guid.CreateVersion7(), Guid.Parse(tenantId), kind, version, content, authorizationQuestion, policyUrl, Guid.CreateVersion7(),
            DateTimeOffset.UtcNow));
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
    }

    public static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string url, object body) =>
        client.PostAsJsonAsync(url, body, Ct);

    /// <summary>El primer tipo activo y publicado del tenant (los tipos por defecto los siembra el aprovisionador).</summary>
    public static async Task<Guid> FirstTypeIdAsync(QepApiFactory factory, string tenantId) =>
        Guid.Parse((await TextAsync(
            factory,
            "SELECT id::text FROM support.ticket_types WHERE tenant_id = @tenant AND is_active AND is_published ORDER BY position LIMIT 1",
            ("tenant", Guid.Parse(tenantId))))!);

    /// <summary>A default type of the tenant by name (<c>SupportTicketTypeDefaults</c>): «Petición general» has 15 legal business days,
    /// «Soporte técnico» has no term.</summary>
    public static async Task<Guid> TypeIdNamedAsync(QepApiFactory factory, string tenantId, string name) =>
        Guid.Parse((await TextAsync(
            factory,
            "SELECT id::text FROM support.ticket_types WHERE tenant_id = @tenant AND name = @name",
            ("tenant", Guid.Parse(tenantId)),
            ("name", name)))!);

    /// <summary>Owner decision S6: extends a filed ticket's deadline through the domain (<c>SupportTicket.Extend</c>, the same entry the
    /// console's <c>POST …/extension</c> uses), with the tenant's own clock and «now». Returns the new due date.</summary>
    public static async Task<DateOnly?> ExtendTicketAsync(QepApiFactory factory, Guid tenantId, Guid ticketId, int businessDays)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var context = await scope.ServiceProvider.GetRequiredService<SupportTicketDtoComposer>().ReadContextAsync(tenantId, Ct);
        var ticket = await scope.ServiceProvider.GetRequiredService<ISupportTicketRepository>()
            .FindAsync(tenantId, new SupportTicketId(ticketId), Ct);
        Assert.NotNull(ticket);
        ticket.Extend(businessDays, "Falta respuesta del proveedor", Guid.CreateVersion7(), context.Now, context.Clock);
        await scope.ServiceProvider.GetRequiredService<ISupportUnitOfWork>().SaveChangesAsync(Ct);
        return ticket.DueOn;
    }

    /// <summary>Una ciudad cualquiera de Geography (la sembró su migración).</summary>
    public static async Task<Guid> AnyCityIdAsync(QepApiFactory factory) =>
        Guid.Parse((await TextAsync(factory, "SELECT id::text FROM geography.cities ORDER BY id LIMIT 1"))!);

    /// <summary>Un radicado con nombre, de Colombia, con todo lo que pide el formulario por defecto: acepta el aviso de privacidad y
    /// los términos en las versiones dadas (plan 2026-11-15, L16).</summary>
    public static object NamedBody(
        Guid typeId,
        Guid cityId,
        string email,
        string documentType = "CC",
        string documentNumber = "1020304050",
        string token = FakeSupportCaptcha.ValidToken,
        int privacyNoticeVersion = 1,
        int termsVersion = 1,
        bool acceptsTerms = true,
        int formVersion = 1,
        Guid? clientRequestId = null,
        string subject = "No me llegó el pedido") =>
        new
        {
            ticketTypeId = typeId,
            subject,
            description = "Pagué el lunes y todavía no llega.",
            anonymous = false,
            requester = new
            {
                documentType,
                documentNumber,
                firstNames = documentType == "NIT" ? null : "Ana",
                lastNames = documentType == "NIT" ? null : "Gómez",
                businessName = documentType == "NIT" ? "Verde S.A.S." : null,
                address = "Calle 1 # 2-3",
                landlinePhone = (string?)null,
                mobilePhone = "3001234567",
                country = "CO",
                cityId,
                cityName = (string?)null,
                email,
            },
            acceptsDataPolicy = true,
            privacyNoticeVersion,
            anonymousNoticeVersion = (int?)null,
            acceptsTerms,
            termsVersion,
            formVersion,
            turnstileToken = token,
            clientRequestId,
        };

    /// <summary>Un radicado anónimo: sin solicitante ni aviso de privacidad; con el aviso anónimo y los términos.</summary>
    public static object AnonymousBody(
        Guid typeId,
        string token = FakeSupportCaptcha.ValidToken,
        int anonymousNoticeVersion = 1,
        int termsVersion = 1,
        bool acceptsTerms = true,
        Guid? clientRequestId = null) =>
        new
        {
            ticketTypeId = typeId,
            subject = "Denuncia anónima",
            description = "Quiero reportar algo sin dar mis datos.",
            anonymous = true,
            requester = (object?)null,
            acceptsDataPolicy = false,
            privacyNoticeVersion = (int?)null,
            anonymousNoticeVersion,
            acceptsTerms,
            termsVersion,
            formVersion = 1,
            turnstileToken = token,
            clientRequestId,
        };

    /// <summary>Un cliente con documento, insertado por SQL como <see cref="SupportApiHarness.SeedCustomerAsync"/>. El tipo va con el
    /// nombre del enum de Customers (<c>Nit</c>, <c>Cc</c>). <paramref name="mergedInto"/> lo deja fusionado en otro.</summary>
    public static async Task<Guid> SeedIdentifiedCustomerAsync(
        QepApiFactory factory, Guid tenantId, string name, string type, string number, Guid? mergedInto = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            factory,
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, completeness, whatsapp_user_id, with_retention, vat_surplus,
                version, created_at, updated_at, identification_type, identification_number, merged_into_id, merged_at)
            VALUES (@id, @tenant, @name, @active, 'Incomplete', @wa, false, false, 1, now(), now(), @type, @number, @merged, @mergedAt)
            """,
            ("id", id), ("tenant", tenantId), ("name", name), ("active", mergedInto is null), ("wa", $"CO.{id:N}"), ("type", type),
            ("number", number), ("merged", mergedInto), ("mergedAt", mergedInto is null ? null : DateTimeOffset.UtcNow));
        return id;
    }

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

    /// <summary>El correo de apertura: anota en vez de mandar. Cada prueba mira sólo los correos a su dirección.</summary>
    internal RecordingSupportOpeningEmail Emails { get; } = new();

    /// <summary>Fase 4b: el bucket de los PDF de la respuesta formal, en memoria. La consulta firma la descarga desde acá.</summary>
    internal RecordingSupportDocumentStorage Documents { get; } = new();

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

    /// <summary>Para sumar configuración en una clase derivada.</summary>
    protected virtual void ConfigureSettings(Dictionary<string, string> settings)
    {
    }

    public async ValueTask InitializeAsync()
    {
        _database = await StartDatabaseAsync();
        var settings = SupportPublicHarness.Settings(RealAuthentication);
        ConfigureSettings(settings);
        _factory = new QepApiFactory(
            _database.GetConnectionString(),
            services =>
            {
                RemoveThreadFollowWorker(services);
                services.RemoveAll<ISupportCaptcha>();
                services.AddSingleton<ISupportCaptcha>(Captcha);
                services.RemoveAll<ITenantClock>();
                services.AddSingleton<ITenantClock>(Clock);
                services.RemoveAll<ISupportOpeningEmail>();
                services.AddSingleton<ISupportOpeningEmail>(Emails);
                services.RemoveAll<ISupportDocumentStorage>();
                services.AddSingleton<ISupportDocumentStorage>(Documents);
                ConfigureServices(services);
            },
            settings);
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

/// <summary><see cref="SupportPublicHost"/> con todo el log en <c>Trace</c> y capturado en memoria: la prueba de que el PIN no llega
/// a ningún log necesita ver hasta lo más verboso.</summary>
public sealed class SupportPublicLoggedHost : SupportPublicHost
{
    internal CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureSettings(Dictionary<string, string> settings)
    {
        settings["Logging:LogLevel:Default"] = "Trace";
        settings["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace";
    }

    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ILoggerProvider>(Logs);
}

/// <summary>El correo de apertura que anota en vez de mandar. Falla como el canal real (<c>send_failed</c>) para las direcciones de
/// <see cref="FailFor"/>: así una prueba hace fallar el suyo sin tocar el de las demás.</summary>
internal sealed class RecordingSupportOpeningEmail : ISupportOpeningEmail
{
    public ConcurrentQueue<SupportOpeningEmail> Sent { get; } = new();

    public ConcurrentDictionary<string, bool> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Algo que corre antes de «mandar» a esa dirección: una prueba lo usa para romper lo que el handler escribe después.</summary>
    public ConcurrentDictionary<string, Func<Task>> BeforeSend { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SupportOpeningEmail> To(string address) =>
        Sent.Where(email => string.Equals(email.ToAddress, address, StringComparison.OrdinalIgnoreCase)).ToList();

    public async Task<SupportChannelSendResult> SendAsync(SupportOpeningEmail email, CancellationToken cancellationToken)
    {
        if (BeforeSend.TryGetValue(email.ToAddress, out var before))
        {
            await before();
        }

        Sent.Enqueue(email);
        return FailFor.ContainsKey(email.ToAddress)
            ? new SupportChannelSendResult.Failed(null, SupportChannelReasons.SendFailed)
            : new SupportChannelSendResult.Sent(null, null);
    }
}

/// <summary>Todo lo que se loguea, ya formateado y con la excepción, en memoria.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, Entries);

    public void Dispose()
    {
    }

    private sealed class Capturing(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue($"{category} {formatter(state, exception)} {state} {exception}");
    }
}
