using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Catalog.Application;
using Modules.Companies.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Modules.Wholesale.Application;
using Testcontainers.PostgreSql;

namespace Modules.Wholesale.IntegrationTests;

/// <summary>Un tenant sembrado para una prueba: quien llama (<c>SubjectId</c>) y su membresía activa <c>admin</c>.</summary>
internal sealed record WholesaleTestTenant(Guid TenantId, Guid SubjectId, Guid MemberId);

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo (base y fábrica copiadas de <c>PurchasesApiHarness</c>), con
/// un tenant nuevo por prueba (<see cref="NewTenantAsync"/>) para que varias compartan host (<see cref="SharedWholesaleHost"/>).
/// </summary>
internal static class WholesaleApiHarness
{
    public const string MemberName = "Ana Mayorista";

    public const string DistributorInvitedEvent = "wholesale.distributor-invited.v1";

    public const string EmailChallengeEvent = "identity.email-challenge-requested.v1";

    public const string DefaultBusinessName = "Distribuciones Norte SAS";

    /// <summary>La versión vigente de los términos en los hosts de prueba (<c>Terms:CurrentVersion</c>).</summary>
    public const string TermsVersion = "2026-11-19-pruebas";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Spec 2026-11-22: las listas viven en Catalog; la vista previa del distribuidor sigue en Mayorista.</summary>
    public static string PriceListsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/catalog/price-lists";

    public static string PreviewUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/wholesale/price-lists/preview";

    public static string DistributorsUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/wholesale/distributors";

    public static string CategoriesUrl(Guid tenantId) => $"/api/v1/tenants/{tenantId}/wholesale/categories";

    /// <summary>La raíz de las rutas del link; el token va escapado como lo mandaría el navegador.</summary>
    public static string InvitationUrl(string token) => $"/api/v1/wholesale-invitations/{Uri.EscapeDataString(token)}";

    /// <summary>El correo del usuario que siembra <see cref="NewTenantAsync"/>: invitar a ése es invitar a un owner existente.</summary>
    public static string EmailOf(WholesaleTestTenant tenant) => $"mayorista-{tenant.SubjectId:N}@example.com";

    /// <summary>Quien abre el link sin sesión.</summary>
    public static HttpClient CreateAnonymous(QepApiFactory factory) => ClientWithOwnIp(factory);

    /// <summary>
    /// Un cliente con su propia IP (198.18.0.0/15, reservada para pruebas), como <c>SupportPublicHarness</c>. TestServer no trae
    /// <c>RemoteIpAddress</c>, así que sin esto todas las pruebas de un host compartido caen en la misma partición del limitador
    /// <c>Credentials</c> (10 por minuto por ruta) y una clase con varias sesiones por link recibe 429 según el orden.
    /// </summary>
    private static HttpClient ClientWithOwnIp(QepApiFactory factory)
    {
        var next = Interlocked.Increment(ref _nextIp);
        var ip = System.Net.IPAddress.Parse($"198.18.{next / 250 % 250}.{(next % 250) + 1}");
        return new HttpClient(factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = ip))
        {
            BaseAddress = factory.Server.BaseAddress,
        };
    }

    private static int _nextIp;

    /// <summary>
    /// La sesión de quien acepta, con el stub por headers: el sujeto QEP va en <c>X-Subject-Id</c> y el tenant es uno cualquiera
    /// (el stub exige el header, pero las rutas del link no miran el tenant de la sesión).
    /// </summary>
    public static HttpClient CreateSession(QepApiFactory factory, Guid userId, Guid? tenantId = null)
    {
        var client = ClientWithOwnIp(factory);
        client.DefaultRequestHeaders.Add("X-Subject-Id", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", (tenantId ?? Guid.CreateVersion7()).ToString());
        return client;
    }

    /// <summary>Lo que el stub simula de un token de Google: un subject nuevo y el correo, verificado o no.</summary>
    public static HttpClient CreateGoogle(QepApiFactory factory, string email, bool verified = true)
    {
        var client = CreateSession(factory, Guid.CreateVersion7());
        client.DefaultRequestHeaders.Add("X-Email", email);
        client.DefaultRequestHeaders.Add("X-Email-Verified", verified ? "true" : "false");
        return client;
    }

    /// <summary>
    /// Abre sesión con contraseña desde el link y devuelve el id del usuario (lo crea si no existía). Spec 2026-11-19 §4: pide el
    /// código al correo (por defecto, el invitado) y lo manda con la versión de los términos; una cuenta que ya tiene
    /// contraseña los ignora.
    /// </summary>
    public static async Task<Guid> PasswordSessionAsync(
        QepApiFactory factory, string token, string password = "Distri-2026!", string? email = null)
    {
        using var anonymous = CreateAnonymous(factory);
        email ??= (await anonymous.GetFromJsonAsync<global::Api.WholesaleInvitationResponse>(InvitationUrl(token), Ct))!.Email;
        var code = await EmailCodeAsync(factory, token, email);
        var response = await anonymous.PostAsJsonAsync(
            $"{InvitationUrl(token)}/session-with-password", new { email, password, code, termsVersion = TermsVersion }, Ct);
        await EnsureSuccessAsync(response);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("userId").GetGuid();
    }

    /// <summary>
    /// Pide el código de <c>email-code</c> y lo lee del outbox, el único lugar donde existe en claro. <c>null</c> si nunca se
    /// emitió uno para ese correo (por ejemplo, con la invitación ya no pendiente).
    /// </summary>
    public static async Task<string?> EmailCodeAsync(QepApiFactory factory, string token, string email)
    {
        using var anonymous = CreateAnonymous(factory);
        var response = await anonymous.PostAsJsonAsync($"{InvitationUrl(token)}/email-code", new { email }, Ct);
        await EnsureSuccessAsync(response);
        return await LatestEmailCodeAsync(factory, email);
    }

    /// <summary>El código del último desafío emitido para ese correo (normalizado como lo guarda Identity).</summary>
    public static async Task<string?> LatestEmailCodeAsync(QepApiFactory factory, string email)
    {
        var payloads = await RowsAsync<string>(
            factory,
            $"""
            SELECT payload::text AS "Value" FROM platform.outbox_messages
            WHERE event_name = {EmailChallengeEvent} AND payload ->> 'email' = {email.Trim().ToLowerInvariant()}
            ORDER BY occurred_at DESC, id DESC
            """);
        if (payloads.Count == 0)
        {
            return null;
        }

        using var document = JsonDocument.Parse(payloads[0]);
        return document.RootElement.GetProperty("code").GetString();
    }

    /// <summary>
    /// Un usuario activo con una membresía activa de <paramref name="role"/> en un tenant ajeno (sin ser su owner). Devuelve el
    /// id del usuario; su correo es <c>miembro-{id:N}@example.com</c>.
    /// </summary>
    public static async Task<Guid> AddMemberAsync(QepApiFactory factory, Guid tenantId, string role)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var subjectId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var user = User.CreateInvited(UserId.Parse(subjectId.ToString()), $"miembro-{subjectId:N}@example.com", now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(
            MembershipId.New(), user.Id.Value, new Modules.Tenancy.Domain.TenantId(tenantId), [role], "test", now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(Ct);
        await tenancy.SaveChangesAsync(Ct);
        return subjectId;
    }

    /// <summary>Lista, empresa facturadora e invitación: lo que toda prueba de la aceptación necesita del lado del mayorista.</summary>
    public static async Task<(WholesaleTestTenant Wholesaler, DistributorDetailDto Distributor, string Token)> InvitedAsync(
        QepApiFactory factory,
        string email = "ana@distri.co",
        string identificationNumber = "900123456",
        string businessName = DefaultBusinessName,
        string country = "CO",
        string identificationType = "NIT")
    {
        var wholesaler = await NewTenantAsync(factory);
        using var manager = CreateManager(factory, wholesaler);
        var list = await CreatePriceListAsync(manager, wholesaler.TenantId, "Oro", 10m);
        var company = await CreateCompanyAsync(manager, wholesaler.TenantId, "Mayorista Central SAS");
        var (distributor, token) = await InviteAsync(
            factory, manager, wholesaler, list.Id, company, email, identificationNumber, businessName, country,
            identificationType);
        return (wholesaler, distributor, token);
    }

    /// <summary>Repite <paramref name="read"/> hasta que cumpla <paramref name="done"/> o pasen cinco segundos (escrituras
    /// que corren después de la respuesta, como el log de fallas del middleware).</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var value = await read();
        for (var attempt = 0; attempt < 50 && !done(value); attempt++)
        {
            await Task.Delay(100, Ct);
            value = await read();
        }

        return value;
    }

    public static async Task<PostgreSqlContainer> StartDatabaseAsync()
    {
        var database = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase("qep")
            .WithUsername("qep")
            .WithPassword("qep-integration")
            .Build();
        await database.StartAsync(Ct);
        return database;
    }

    /// <summary>
    /// Siembra un tenant nuevo con sus módulos (todos por defecto), el usuario que llama y su membresía activa <c>admin</c> con
    /// nombre «Ana Mayorista», el owner del tenant.
    /// </summary>
    public static async Task<WholesaleTestTenant> NewTenantAsync(
        QepApiFactory factory, IEnumerable<TenantModuleKey>? modules = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var tenantId = new Modules.Tenancy.Domain.TenantId(Guid.CreateVersion7());
        var subjectId = Guid.CreateVersion7();
        var membershipId = MembershipId.New();
        var now = DateTimeOffset.UtcNow;

        tenancy.Tenants.Add(Tenant.Create(
            tenantId,
            $"wholesale-{tenantId.Value:N}",
            "Mayorista API Tests",
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            membershipId,
            now));
        await tenancy.SaveChangesAsync(Ct);

        foreach (var key in modules ?? TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({tenantId.Value}, {key.Value}, {now}, {TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                Ct);
        }

        var user = User.CreateInvited(UserId.Parse(subjectId.ToString()), $"mayorista-{subjectId:N}@example.com", now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(membershipId, user.Id.Value, tenantId, ["admin"], "test", now);
        membership.UpdateProfile(MemberName, null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(Ct);
        await tenancy.SaveChangesAsync(Ct);

        return new WholesaleTestTenant(tenantId.Value, subjectId, membership.Id.Value);
    }

    public static HttpClient CreateClient(QepApiFactory factory, Guid subjectId, Guid tenantId, params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId.ToString());
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    /// <summary>Gestiona listas y distribuidores y, para sembrar, empresas y catálogo.</summary>
    public static HttpClient CreateManager(QepApiFactory factory, WholesaleTestTenant tenant) =>
        CreateClient(
            factory,
            tenant.SubjectId,
            tenant.TenantId,
            WholesalePermissions.PriceListManage,
            WholesalePermissions.DistributorRead,
            WholesalePermissions.DistributorManage,
            CompaniesPermissions.CompanyRead,
            CompaniesPermissions.CompanyManage,
            CatalogPermissions.ProductRead,
            CatalogPermissions.ProductManage);

    public static HttpClient CreateReader(QepApiFactory factory, WholesaleTestTenant tenant) =>
        CreateClient(factory, tenant.SubjectId, tenant.TenantId, WholesalePermissions.DistributorRead);

    /// <summary>Una empresa facturadora con NIT en Medellín (o la primera ciudad sembrada si no está).</summary>
    public static async Task<Guid> CreateCompanyAsync(HttpClient client, Guid tenantId, string name, string taxId = "900111222")
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/companies",
            new
            {
                name,
                bankAccounts = new[] { new { bankName = "Bancolombia", accountNumber = "123456789", currency = "COP" } },
                taxId,
                country = "CO",
                documentType = "NIT",
                phone = "3001234567",
                email = "ventas@mayorista.co",
                address = "Calle 10 # 40-20",
                cityId = await MedellinAsync(client),
            },
            Ct);
        await EnsureSuccessAsync(response);
        return await ReadIdAsync(response);
    }

    public static async Task<Guid> CreateCategoryAsync(HttpClient client, Guid tenantId, string name, Guid? parentId = null)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/categories", new { name, parentId }, Ct);
        await EnsureSuccessAsync(response);
        return await ReadIdAsync(response);
    }

    /// <summary>
    /// Un producto con una sola variante (copia de <c>PurchasesApiHarness.CreateVariantAsync</c>); devuelve el id de la variante.
    /// <paramref name="priceCop"/> nulo deja la variante sin precio en COP (con uno en USD).
    /// </summary>
    public static async Task<Guid> CreateVariantAsync(
        HttpClient client, Guid tenantId, string code, string name, decimal? priceCop, Guid? categoryId = null)
    {
        var prices = priceCop is { } cop
            ? new Dictionary<string, decimal> { ["COP"] = cop }
            : new Dictionary<string, decimal> { ["USD"] = 10m };
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenantId}/catalog/products",
            new
            {
                name,
                categoryId,
                variants = new[]
                {
                    new
                    {
                        code,
                        pricing = new
                        {
                            prices,
                            scales = new object[]
                            {
                                new { fromUnit = 1, toUnit = 999_999, discount = 0m, restriction = "multiple", multiple = 1 },
                            },
                            packagingUnits = Array.Empty<int>(),
                        },
                    },
                },
            },
            Ct);
        await EnsureSuccessAsync(response);
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    public static async Task<PriceListDetailDto> CreatePriceListAsync(
        HttpClient client, Guid tenantId, string name, decimal discountPercent, string visibility = "AllActive", params Guid[] categoryIds)
    {
        var response = await client.PostAsJsonAsync(
            PriceListsUrl(tenantId), new { name, adjustmentPercent = -discountPercent, visibility, categoryIds }, Ct);
        await EnsureSuccessAsync(response);
        return (await response.Content.ReadFromJsonAsync<PriceListDetailDto>(Ct))!;
    }

    /// <summary>Invita y devuelve el detalle y el token plano, leído del outbox (es el único lugar donde existe).</summary>
    public static async Task<(DistributorDetailDto Distributor, string Token)> InviteAsync(
        QepApiFactory factory,
        HttpClient client,
        WholesaleTestTenant tenant,
        Guid priceListId,
        Guid billingCompanyId,
        string email = "ana@distri.co",
        string identificationNumber = "900123456",
        string businessName = DefaultBusinessName,
        string country = "CO",
        string identificationType = "NIT")
    {
        var response = await client.PostAsJsonAsync(
            DistributorsUrl(tenant.TenantId),
            new
            {
                email,
                businessName,
                country,
                identificationType,
                identificationNumber,
                contactName = "Ana Pérez",
                priceListId,
                advisorId = tenant.MemberId,
                billingCompanyId,
                // La ciudad que el país exige: el id de Geography en Colombia, el texto afuera.
                cityId = country == "CO" ? await MedellinAsync(client) : (Guid?)null,
                cityName = country == "CO" ? null : "Madrid",
                address = "Calle 10 # 40-20",
            },
            Ct);
        await EnsureSuccessAsync(response);
        var distributor = (await response.Content.ReadFromJsonAsync<DistributorDetailDto>(Ct))!;
        return (distributor, await LatestTokenAsync(factory, distributor.Id));
    }

    /// <summary>El token del último evento de invitación de ese distribuidor (un reenvío publica otro).</summary>
    public static async Task<string> LatestTokenAsync(QepApiFactory factory, Guid distributorId)
    {
        using var document = await LatestInvitationPayloadAsync(factory, distributorId);
        return document.RootElement.GetProperty("token").GetString()!;
    }

    /// <summary>
    /// La prueba de buzón del último evento de invitación (spec 2026-11-24), la que el correo manda como <c>?ev=</c>;
    /// <c>null</c> si el evento no la trae.
    /// </summary>
    public static async Task<string?> LatestEmailProofAsync(QepApiFactory factory, Guid distributorId)
    {
        using var document = await LatestInvitationPayloadAsync(factory, distributorId);
        return document.RootElement.TryGetProperty("emailProof", out var emailProof) ? emailProof.GetString() : null;
    }

    private static async Task<JsonDocument> LatestInvitationPayloadAsync(QepApiFactory factory, Guid distributorId)
    {
        var payloads = await RowsAsync<string>(
            factory,
            $"""
            SELECT payload::text AS "Value" FROM platform.outbox_messages
            WHERE event_name = {DistributorInvitedEvent} AND payload ->> 'distributorId' = {distributorId.ToString()}
            ORDER BY occurred_at DESC, id DESC
            """);
        return JsonDocument.Parse(payloads[0]);
    }

    /// <summary>El <c>If-Match</c> de las escrituras sobre un recurso existente, con la forma fuerte <c>"7"</c>.</summary>
    public static HttpRequestMessage WithIfMatch(HttpRequestMessage request, long version)
    {
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return request;
    }

    public static async Task<IReadOnlyList<T>> RowsAsync<T>(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        return await tenancy.Database.SqlQuery<T>(sql).ToListAsync(Ct);
    }

    public static async Task ExecuteAsync(QepApiFactory factory, FormattableString sql)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await tenancy.Database.ExecuteSqlInterpolatedAsync(sql, Ct);
    }

    public static async Task<string?> CodeOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(Ct);

    public static async Task<Guid> MedellinAsync(HttpClient client)
    {
        Guid? first = null;
        var departments = await client.GetFromJsonAsync<List<GeographyRow>>("/api/v1/departments", Ct) ?? [];
        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyRow>>($"/api/v1/cities?departmentId={department.Id}", Ct) ?? [];
            first ??= cities.FirstOrDefault()?.Id;
            var medellin = cities.FirstOrDefault(city => city.Name.StartsWith("Medell", StringComparison.OrdinalIgnoreCase));
            if (medellin is not null)
            {
                return medellin.Id;
            }
        }

        return first ?? throw new InvalidOperationException("No seeded DIVIPOLA city.");
    }

    /// <summary>Una ciudad sembrada distinta de <paramref name="excluded"/>.</summary>
    public static async Task<Guid> OtherCityAsync(HttpClient client, Guid excluded)
    {
        var departments = await client.GetFromJsonAsync<List<GeographyRow>>("/api/v1/departments", Ct) ?? [];
        foreach (var department in departments)
        {
            var cities = await client.GetFromJsonAsync<List<GeographyRow>>($"/api/v1/cities?departmentId={department.Id}", Ct) ?? [];
            if (cities.FirstOrDefault(city => city.Id != excluded) is { } other)
            {
                return other.Id;
            }
        }

        throw new InvalidOperationException("No second seeded DIVIPOLA city.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{(int)response.StatusCode} {response.RequestMessage?.RequestUri}: {await BodyAsync(response)}");
        }
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private sealed record GeographyRow(Guid Id, string Name);

    /// <param name="configureServices">Sustituciones de servicios para la prueba; corre después del registro de la aplicación.</param>
    /// <param name="settings">Configuración que la prueba fija por encima de <c>appsettings.json</c>.</param>
    public sealed class QepApiFactory(
        string connectionString,
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string>? settings = null)
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes el validador de Notifications falla
            // al arrancar.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true y sembrar datos en la base de pruebas.
            builder.UseSetting("Seed:Enabled", "false");
            // Sin versión configurada, el alta de un distribuidor nuevo falla cerrado (spec 2026-11-19).
            builder.UseSetting("Terms:CurrentVersion", TermsVersion);

            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                builder.UseSetting(key, value);
            }

            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }
        }
    }
}
