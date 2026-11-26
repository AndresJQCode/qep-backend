using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Modules.Authorization.Application;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Support.Application;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Support.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de integración del módulo. Copia el de Loans: un
/// tenant con fila (sin ella no hay máscara de módulos ni reloj del tenant) y
/// <c>configureServices</c> en la fábrica. <see cref="NewTenantId"/> da un tenant por prueba, para el host compartido.
/// </summary>
internal static class SupportApiHarness
{
    public const string TenantId = "01900000-0000-7000-8000-000000000001";
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";
    public const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    public const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    public static string SupportUrl(string tenantId = TenantId) =>
        $"/api/v1/tenants/{tenantId}/support";

    /// <summary>Un tenant nuevo por prueba: las que comparten host (<see cref="SharedSupportHost"/>) no se pisan.</summary>
    public static string NewTenantId() => Guid.CreateVersion7().ToString();

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

    /// <summary>
    /// Siembra la fila del tenant, sus roles de fábrica y sus módulos. Inserta únicamente las claves que faltan, por lo
    /// que se puede llamar dos veces sobre el mismo tenant.
    /// </summary>
    public static async Task SeedTenantAsync(
        QepApiFactory factory,
        string tenantId = TenantId,
        IEnumerable<TenantModuleKey>? modules = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId));
        var now = DateTimeOffset.UtcNow;
        var existing = await tenancy.Tenants.FindAsync(
            [id], TestContext.Current.CancellationToken);
        if (existing is null)
        {
            tenancy.Tenants.Add(Tenant.Create(
                id,
                // El slug es único en la base: un segundo tenant necesita el suyo.
                tenantId == TenantId ? "support-api-tests" : $"support-api-{tenantId[^12..]}",
                "Support API Tests",
                "es-CO",
                "America/Bogota",
                "yyyy-MM-dd",
                MembershipId.New(),
                now));
            await tenancy.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // El tenant se inserta directo y después del arranque, así que nace sin los roles que el alta le sembraría.
        await using var seeding = factory.Services.CreateAsyncScope();
        await seeding.ServiceProvider.GetRequiredService<ITenantRoleSeeder>()
            .SeedAsync(id.Value, TestContext.Current.CancellationToken);

        foreach (var key in modules ?? TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({id.Value}, {key.Value}, {now}, {TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Inserta en <c>customers.customers</c> lo mínimo para que el cliente exista; Support lo
    /// referencia por id suelto, así que las pruebas no necesitan pasar por el módulo Customers.
    /// </summary>
    public static async Task<Guid> SeedCustomerAsync(
        QepApiFactory factory,
        Guid tenantId,
        string name,
        string? phoneE164 = null,
        string? email = null)
    {
        var id = Guid.CreateVersion7();
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO customers.customers (id, tenant_id, name, is_active, phone_e164, email, completeness, whatsapp_user_id, with_retention, vat_surplus, version, created_at, updated_at)
            VALUES (@id, @tenantId, @name, true, @phone, @email, 'Incomplete', @whatsAppUserId, false, false, 1, now(), now())
            """,
            connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("tenantId", tenantId);
        insert.Parameters.AddWithValue("name", name);
        // 'Incomplete' porque CK_customers_complete_fields (CustomersDbContext.CompleteFieldsCheck) exige CUC, documento,
        // y país a una ficha 'Complete'; Support sólo necesita nombre, teléfono y correo.
        // Único por llamada: Customers puede exigir un whatsapp_user_id distinto por cliente.
        insert.Parameters.AddWithValue("whatsAppUserId", $"CO.{id:N}");
        insert.Parameters.AddWithValue("phone", (object?)phoneE164 ?? DBNull.Value);
        insert.Parameters.AddWithValue("email", (object?)email ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    /// <summary>Deja al cliente inactivo: Support no le vincula tickets nuevos.</summary>
    public static async Task DeactivateCustomerAsync(QepApiFactory factory, Guid customerId)
    {
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var update = new NpgsqlCommand(
            "UPDATE customers.customers SET is_active = false WHERE id = @id", connection);
        update.Parameters.AddWithValue("id", customerId);
        await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Borra la fila del cliente con SQL, sin pasar por Customers: simula un cliente que ya no se resuelve.</summary>
    public static async Task DeleteCustomerAsync(QepApiFactory factory, Guid customerId)
    {
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var delete = new NpgsqlCommand("DELETE FROM customers.customers WHERE id = @id", connection);
        delete.Parameters.AddWithValue("id", customerId);
        await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Siembra una membresía del tenant (con su usuario) con un rol de fábrica: <c>admin</c> y <c>advisor</c> conceden
    /// <c>support.ticket.manage</c>, <c>billing</c> no. <paramref name="removed"/> la deja quitada.
    /// </summary>
    public static async Task<Guid> SeedMemberAsync(
        QepApiFactory factory,
        string email,
        string? displayName,
        string role,
        bool removed = false,
        string tenantId = TenantId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var tenant = await tenancy.Tenants.FindAsync([new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId))], ct);
        var now = DateTimeOffset.UtcNow;

        var user = User.CreateInvited(UserId.New(), email, now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(
            MembershipId.New(), user.Id.Value, tenant!.Id, [role], "test", now);
        if (displayName is not null)
        {
            membership.UpdateProfile(displayName, null, now);
        }

        if (removed)
        {
            membership.Remove(tenant, now);
        }

        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(ct);
        await tenancy.SaveChangesAsync(ct);
        return membership.Id.Value;
    }

    /// <summary>
    /// La membresía activa de quien llama (<see cref="SubjectId"/>) en el tenant: la que actúa sobre un ticket y la que
    /// resuelve <c>assignee=me</c>. Sin ella el stub sólo trae permisos y el handler responde 403.
    /// </summary>
    /// <remarks>Con el host compartido cada prueba pasa su propio <paramref name="subjectId"/>: el usuario es único en la base.</remarks>
    public static async Task<Guid> SeedCallerMembershipAsync(
        QepApiFactory factory, string displayName, string role = "admin", string tenantId = TenantId, string subjectId = SubjectId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var user = User.CreateInvited(UserId.Parse(subjectId), $"caller-{subjectId[^12..]}-{tenantId[^12..]}@example.com", now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(
            MembershipId.New(), user.Id.Value, new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId)), [role], "test", now);
        membership.UpdateProfile(displayName, null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(ct);
        await tenancy.SaveChangesAsync(ct);
        return membership.Id.Value;
    }

    /// <summary>La cantidad de filas de <c>audit.entries</c> de una acción sobre un recurso: la auditoría atómica no tiene API de lectura acá.</summary>
    public static async Task<int> CountAuditEntriesAsync(QepApiFactory factory, string action, string resourceId)
    {
        var connectionString = factory.Services
            .GetRequiredService<IConfiguration>()
            .GetConnectionString("QepDatabase")!;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM audit.entries WHERE action = @action AND resource_id = @resourceId",
            connection);
        count.Parameters.AddWithValue("action", action);
        count.Parameters.AddWithValue("resourceId", resourceId);
        return (int)(long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está, así que el permiso
    // de support se pide explícito: cada 403 queda atribuible al permiso o al tenant.
    public static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId,
        params string[] permissions)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    public static HttpClient CreateManager(QepApiFactory factory, string tenantId = TenantId) =>
        CreateClient(factory, SubjectId, tenantId, SupportPermissions.Read, SupportPermissions.Manage);

    public static HttpClient CreateReader(QepApiFactory factory, string tenantId = TenantId) =>
        CreateClient(factory, SubjectId, tenantId, SupportPermissions.Read);

    /// <summary>Abre base, fábrica y tenant sembrado: el arranque que repiten todas las pruebas.</summary>
    public static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenAsync(
        IEnumerable<TenantModuleKey>? modules = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(database.GetConnectionString());
        await SeedTenantAsync(factory, modules: modules);
        return (database, factory);
    }

    /// <summary>La configuración de un host propio cuyo seguimiento del hilo no corre solo: la prueba llama
    /// <c>SupportThreadFollower.ProcessBatchAsync</c> a mano, y el feed no se vacía por detrás.</summary>
    public static readonly IReadOnlyDictionary<string, string> WorkersOff =
        new Dictionary<string, string> { ["Support:Workers:Enabled"] = "false" };

    /// <summary>Base y fábrica propias, con el seguimiento apagado: para las pruebas que leen o escriben toda la base (el feed) o
    /// cambian servicios.</summary>
    public static async Task<(PostgreSqlContainer Database, QepApiFactory Factory)> OpenOwnHostAsync(
        Action<IServiceCollection>? configureServices = null)
    {
        var database = await StartDatabaseAsync();
        var factory = new QepApiFactory(
            database.GetConnectionString(),
            services =>
            {
                RemoveThreadFollowWorker(services);
                configureServices?.Invoke(services);
            },
            WorkersOff);
        return (database, factory);
    }

    /// <summary>Saca los hosted services de Soporte por su tipo (precedente <c>PipelineApiHarness</c>), además de la bandera
    /// <c>Support:Workers:Enabled</c>: si la bandera dejara de leerse, la prueba sigue sin un worker que le gane la carrera. Fase 4b: también
    /// el de la encuesta (<see cref="RemoveSurveyWorker"/>).</summary>
    public static void RemoveThreadFollowWorker(IServiceCollection services)
    {
        RemoveSurveyWorker(services);
        var workers = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(Modules.Support.Infrastructure.SupportThreadFollowWorker))
            .ToList();
        foreach (var descriptor in workers)
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>Fase 4b (Tareas 11 y 14): saca el worker de la encuesta y el del cierre automático. Cada resolución pide una encuesta, y
    /// (spec 2026-11-22: también el del aviso de «sin asignar», que mira tickets de todos los tenants del host) el worker la procesaría por detrás (la salta con survey_disabled, o la manda) mientras la prueba la mira; y el cierre automático
    /// cerraría los tickets resueltos de una prueba que adelanta el reloj del tenant. Las pruebas de los dos los corren a mano.</summary>
    public static void RemoveSurveyWorker(IServiceCollection services)
    {
        var workers = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && (descriptor.ImplementationType == typeof(Modules.Support.Infrastructure.SupportSurveyWorker)
                    || descriptor.ImplementationType == typeof(Modules.Support.Infrastructure.SupportAutoCloseWorker)
                    || descriptor.ImplementationType == typeof(Modules.Support.Infrastructure.SupportUnassignedAlertWorker)))
            .ToList();
        foreach (var descriptor in workers)
        {
            services.Remove(descriptor);
        }
    }

    /// <summary>
    /// Spec 2026-11-22: a real business-hours schedule of the tenant (Monday to Friday 08:00-18:00, the rest closed), created through the
    /// Messaging endpoint the way a tenant does, so the provider reads what the product stores. Returns its id.
    /// </summary>
    public static async Task<Guid> CreateOfficeScheduleAsync(QepApiFactory factory, string tenant, string name)
    {
        using var client = CreateClient(
            factory, SubjectId, tenant, "messaging.conversation.read", "messaging.business_hours.manage");
        var weekly = Enumerable.Range(1, 7)
            .Select(day => new { day, ranges = day <= 5 ? new[] { new { from = 480, to = 1080 } } : [] })
            .ToArray();
        var response = await client.PostAsJsonAsync(
            $"/api/v1/tenants/{tenant}/messaging/business-hours",
            new { name, weekly, exceptions = Array.Empty<object>(), holidayCountry = (string?)null, connectionIds = Array.Empty<Guid>() },
            TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();
    }

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    // Desde el texto y no desde el stream: así se puede leer el mismo cuerpo dos veces (el código y después los errores).
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.Clone();
    }

    /// <summary>El <c>code</c> del problema (contrato de <c>ApiExceptionHandler</c>).</summary>
    public static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).TryGetProperty("code", out var code) ? code.GetString() : null;

    /// <summary>Las claves del mapa <c>errors</c> de un 422 <c>validation.failed</c>.</summary>
    public static async Task<IReadOnlyList<string>> ErrorKeysAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("errors").EnumerateObject().Select(property => property.Name).ToList();

    /// <summary>Un <c>PUT</c> con <c>If-Match</c>; <paramref name="version"/> null lo manda sin el header.</summary>
    public static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body, long? version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (version is { } value)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{value}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <param name="configureServices">
    /// Sustituciones de servicios para la prueba; corre después del registro de la aplicación.
    /// </param>
    /// <param name="settings">Claves de configuración extra, por ejemplo <c>Support:Workers:Enabled</c> = <c>false</c> para que el
    /// seguimiento del hilo no corra solo y la prueba lo llame a mano.</param>
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes el
            // validador de Notifications falla al arrancar. SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true y sembrar datos
            // en el tenant de pruebas. El harness no depende del entorno del shell.
            builder.UseSetting("Seed:Enabled", "false");
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

/// <summary>Reloj del tenant movible: las pruebas fijan «hoy» y lo adelantan sin esperar al calendario real.</summary>
internal sealed class MutableTenantClock(DateTimeOffset utcNow) : ITenantClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public Task<TenantCalendar> GetAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(new TenantCalendar(UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Bogota")));
}
