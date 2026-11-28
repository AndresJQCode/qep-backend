using System.Collections.Concurrent;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using static Modules.Tenancy.IntegrationTests.TenancyTestDatabase;

namespace Modules.Tenancy.IntegrationTests;

/// <summary>
/// Cuántos comandos SQL ejecuta un request, de punta a punta y con la autenticación real (cookie <c>qep_session</c>): el
/// mismo camino que producción, donde cada consulta en serie paga la red entre el pod y Postgres (10-20 ms). El interceptor
/// cuenta en <b>todos</b> los <c>DbContext</c> de módulo y agrupa por el header <c>X-Query-Budget</c> del request, así los workers
/// del host no suman. Un presupuesto que se pasa es una regresión: alguien volvió a resolver algo por fila o a consultar lo que
/// la caché ya tenía.
/// </summary>
/// <remarks>
/// Medido el 2026-11-17 con este mismo interceptor sobre <c>develop</c> (f96d59f9), antes del cambio, con el owner y cuatro
/// invitados en el tenant: <b>12 comandos</b> en <c>GET /memberships?search=</c>, o sea 7 + N (sesión, membresía, estado del
/// tenant, roles, módulos, tenant, roster y un <c>identity.users</c> por miembro). En producción, con doce miembros, eran los 19
/// que medía Tempo. Después del cambio, el request con la caché ya caliente son 3: tenant, roster y un solo
/// <c>identity.users</c> por lote; el frío, 8.
/// </remarks>
public sealed class RequestQueryBudgetTests
{
    private const string Issuer = "https://accounts.google.com";
    private const string Audience = "test-audience";
    private const string BudgetHeader = "X-Query-Budget";
    private static readonly byte[] SigningKeyBytes = RandomNumberGenerator.GetBytes(32);
    private static readonly string SigningKeyBase64 = Convert.ToBase64String(SigningKeyBytes);
    private static readonly string[] AdvisorRoles = ["advisor"];
    private static readonly string[] AdminRoles = ["admin"];
    private static readonly string[] BillingRoles = ["billing"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListingMembershipsWithSearchStaysWithinItsQueryBudget()
    {
        await using var database = await StartDatabaseAsync();
        var counter = new RequestCommandCounter();
        using var factory = new BudgetFactory(database.GetConnectionString(), counter);
        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);

        // El primer request del owner al tenant resuelve la sesión y los permisos en Postgres y los deja en la caché del
        // pod (registrar el tenant no los resuelve: ese request no lleva X-Tenant-Id).
        var cold = await ListAsync(owner, tenantId, "cold");
        Assert.Equal(HttpStatusCode.OK, cold.StatusCode);
        var coldCommands = counter.CommandsOf("cold");
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine(Describe("frío", 8, coldCommands));
        // Sesión, membresía, estado del tenant, roles, módulos, tenant, roster y el lote de correos: 8.
        Assert.True(coldCommands.Count <= 8, Describe("frío", 8, coldCommands));

        for (var i = 0; i < 4; i++)
        {
            await InviteAsync(owner, tenantId, $"ana-{i}-{Guid.NewGuid():N}@example.com", $"Ana {i}");
        }

        var warm = await ListAsync(owner, tenantId, "warm");
        Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
        var warmCommands = counter.CommandsOf("warm");
        output?.WriteLine(Describe("caliente", 5, warmCommands));
        // Tenant, roster y el lote de correos: la sesión y los permisos salen de la caché.
        Assert.True(warmCommands.Count <= 5, Describe("caliente", 5, warmCommands));
    }

    // La caché de permisos del pod se invalida con el cambio, no con el tiempo: el handler que cambia los roles sube la versión
    // de acceso del tenant antes y después de guardar, y el siguiente request de la persona ya ve los permisos nuevos.
    [Fact]
    public async Task AMembershipRoleChangeIsVisibleOnTheNextRequestDespiteTheCache()
    {
        await using var database = await StartDatabaseAsync();
        var counter = new RequestCommandCounter();
        using var factory = new BudgetFactory(database.GetConnectionString(), counter);
        var (owner, tenantId) = await RegisterOwnerAndTenantAsync(factory);
        var (member, membership) = await RegisterSecondUserAsActiveMemberAsync(factory, owner, tenantId);
        Assert.Equal(HttpStatusCode.Forbidden, (await ListAsync(member, tenantId, "before")).StatusCode);

        using var update = new HttpRequestMessage(
            HttpMethod.Put, $"/api/v1/tenants/{tenantId}/memberships/{membership.Id}/roles")
        {
            Content = JsonContent.Create(new { roles = AdminRoles }),
        };
        update.Headers.Add("X-Tenant-Id", tenantId.ToString());
        update.Headers.Add("X-Qep-Client", "web");
        update.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{membership.Version}\""));
        Assert.Equal(HttpStatusCode.OK, (await owner.SendAsync(update, Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ListAsync(member, tenantId, "after")).StatusCode);
    }

    private static string Describe(string label, int budget, IReadOnlyList<string> commands) =>
        $"El request en {label} ejecutó {commands.Count} comandos y el presupuesto es {budget}:{Environment.NewLine}"
        + string.Join(Environment.NewLine, commands.Select((command, index) => $"  {index + 1}. {Compact(command)}"));

    private static string Compact(string sql)
    {
        var flat = string.Join(' ', sql.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));
        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }

    private static async Task<HttpResponseMessage> ListAsync(HttpClient client, Guid tenantId, string probe)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenants/{tenantId}/memberships?search=ana");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        request.Headers.Add(BudgetHeader, probe);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<MembershipPayload> InviteAsync(
        HttpClient owner, Guid tenantId, string email, string displayName, string[]? roles = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, displayName, roles = roles ?? AdvisorRoles }),
        };
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        request.Headers.Add("X-Qep-Client", "web");
        var response = await owner.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MembershipPayload>(Ct))!;
    }

    /// <summary>
    /// Una segunda persona con sesión propia y membresía activa en el tenant del owner: invitada como <c>billing</c> (sin
    /// <c>advisorship.read</c>) y entrando de verdad por <c>/auth/session</c>, que acepta la invitación, igual que en
    /// <c>RealAuthenticationApiTests</c>. La membresía que vuelve es la de después de aceptar, con su versión.
    /// </summary>
    private static async Task<(HttpClient Client, MembershipPayload Membership)> RegisterSecondUserAsActiveMemberAsync(
        BudgetFactory factory, HttpClient owner, Guid tenantId)
    {
        var email = NewEmail();
        var invited = await InviteAsync(owner, tenantId, email, "Ana Invitada", BillingRoles);

        var member = NewClient(factory);
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/session");
        login.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", IssueGoogleIdToken(Guid.NewGuid().ToString(), email));
        login.Headers.Add("X-Qep-Client", "web");
        Assert.Equal(HttpStatusCode.OK, (await member.SendAsync(login, Ct)).StatusCode);

        using var list = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenants/{tenantId}/memberships");
        list.Headers.Add("X-Tenant-Id", tenantId.ToString());
        var roster = await (await owner.SendAsync(list, Ct)).Content.ReadFromJsonAsync<MembershipListPayload>(Ct);
        return (member, roster!.Items.Single(item => item.Id == invited.Id));
    }

    private static HttpClient NewClient(BudgetFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    private static async Task<(HttpClient Client, Guid TenantId)> RegisterOwnerAndTenantAsync(BudgetFactory factory)
    {
        var client = NewClient(factory);
        var token = IssueGoogleIdToken(Guid.NewGuid().ToString(), NewEmail());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register-tenant")
        {
            Content = JsonContent.Create(new
            {
                displayName = "Query Budget Co",
                slug = $"budget-{Guid.NewGuid():N}"[..24],
                defaultCulture = "es-CO",
                timeZone = "America/Bogota",
                dateFormat = "yyyy-MM-dd",
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Qep-Client", "web");
        var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisterTenantPayload>(Ct);
        return (client, registered!.TenantId);
    }

    private static string NewEmail() => $"budget-{Guid.NewGuid():N}@example.com";

    private static string IssueGoogleIdToken(string subject, string email)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: [new Claim("sub", subject), new Claim("email", email), new Claim("email_verified", "true")],
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(30),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(SigningKeyBytes), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record RegisterTenantPayload(Guid TenantId, Guid OwnerUserId);

    private sealed record MembershipPayload(Guid Id, Guid UserId, long Version);

    private sealed record MembershipListPayload(IReadOnlyList<MembershipPayload> Items);

    /// <summary>
    /// Cuenta cada comando SQL que ejecuta cualquier <c>DbContext</c> de módulo, agrupado por el valor del header
    /// <see cref="BudgetHeader"/> del request en curso. Sin request (workers, arranque) o sin header no cuenta nada.
    /// </summary>
    public sealed class RequestCommandCounter : DbCommandInterceptor
    {
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _byProbe = new(StringComparer.Ordinal);

        public IHttpContextAccessor? HttpContextAccessor { get; set; }

        public IReadOnlyList<string> CommandsOf(string probe) =>
            _byProbe.TryGetValue(probe, out var commands) ? [.. commands] : [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command);
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            var probe = HttpContextAccessor?.HttpContext?.Request.Headers[BudgetHeader].ToString();
            if (string.IsNullOrEmpty(probe))
            {
                return;
            }

            _byProbe.GetOrAdd(probe, _ => new ConcurrentQueue<string>()).Enqueue(command.CommandText);
        }
    }

    /// <summary>
    /// El host de auth real (sin el stub), como <c>RealAuthenticationApiTests.QepApiFactory</c>, con el contador en cada
    /// <c>DbContext</c> de módulo: se descubren en el contenedor, como hace <c>QueueDoorbellRegistration</c>, así un módulo
    /// nuevo queda contado sin tocar esta prueba.
    /// </summary>
    private sealed class BudgetFactory(string connectionString, RequestCommandCounter counter) : WebApplicationFactory<Program>
    {
        private static readonly MethodInfo AddToContext =
            typeof(BudgetFactory).GetMethod(nameof(AddCounterTo), BindingFlags.NonPublic | BindingFlags.Static)!;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("IntegrationTests");
            builder.UseSetting("Wms:InventoryEvents:StartAt", "2026-01-01T00:00:00+00:00");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("Seed:Enabled", "false");
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            builder.UseSetting("Store:Storefront:SharedSecret", "test-storefront-shared-secret-0123456789");
            builder.UseSetting("Store:Turnstile:SecretKey", "test-turnstile-secret");
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
            builder.UseSetting("Authentication:Audience", Audience);
            builder.UseSetting("Authentication:TestSigningKey", SigningKeyBase64);
            builder.UseSetting("Platform:OperatorTenantId", string.Empty);
            // Las cachés del pod, como en producción hoy (áreas de Valkey apagadas): es lo que se mide.
            builder.UseSetting("Valkey:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                var contexts = services
                    .Select(descriptor => descriptor.ServiceType)
                    .Where(type => typeof(DbContext).IsAssignableFrom(type)
                        && type != typeof(DbContext)
                        && type.Namespace?.StartsWith("Modules.", StringComparison.Ordinal) == true)
                    .Distinct()
                    .ToList();
                Assert.NotEmpty(contexts);
                services.AddSingleton(counter);
                foreach (var context in contexts)
                {
                    AddToContext.MakeGenericMethod(context).Invoke(null, [services]);
                }
            });
        }

        private static void AddCounterTo<TContext>(IServiceCollection services)
            where TContext : DbContext =>
            services.ConfigureDbContext<TContext>((provider, options) =>
            {
                var counter = provider.GetRequiredService<RequestCommandCounter>();
                counter.HttpContextAccessor ??= provider.GetRequiredService<IHttpContextAccessor>();
                options.AddInterceptors(counter);
            });
    }
}
