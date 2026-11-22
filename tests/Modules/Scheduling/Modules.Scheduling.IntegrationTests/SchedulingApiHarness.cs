using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Identity.Domain;
using Modules.Identity.Infrastructure.Persistence;
using Modules.Scheduling.Application;
using Modules.Scheduling.Domain;
using Modules.Scheduling.Infrastructure.Persistence;
using Modules.Tenancy.Domain;
using Modules.Tenancy.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Session = Modules.Scheduling.Domain.Session;
using SessionId = Modules.Scheduling.Domain.SessionId;

namespace Modules.Scheduling.IntegrationTests;

/// <summary>
/// El arranque compartido de las pruebas de la API de la Agenda. Calco de <c>HrApiHarness</c>: una base por clase, un tenant con fila
/// (sin ella no hay máscara de módulos ni reloj del tenant) y clientes con el stub de permisos por encabezados.
/// </summary>
internal static class SchedulingApiHarness
{
    public const string SubjectId = "01900000-0000-7000-8000-000000000002";

    // Los dos últimos dígitos del id deben ser distintos: el slug del tenant sale de ellos.
    private static int _tenantCounter = 0x10;

    public static readonly string[] AllPermissions =
    [
        SchedulingPermissions.AppointmentRead,
        SchedulingPermissions.AppointmentManage,
        SchedulingPermissions.AvailabilityManage,
        SchedulingPermissions.AgendaReadAll,
        SchedulingPermissions.SettingsManage,
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string SchedulingUrl(string tenantId) => $"/api/v1/tenants/{tenantId}/scheduling";

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

    /// <summary>Un tenant nuevo con la Agenda prendida, su propio id y un cliente con todos los permisos de la Agenda.</summary>
    public static async Task<SchedulingTenant> NewTenantAsync(QepApiFactory factory, string name = "Consultorio Andino")
    {
        var idText = $"01900000-0000-7000-8000-0000000000{Interlocked.Increment(ref _tenantCounter):x2}";
        await SeedTenantAsync(factory, idText, name);
        return new SchedulingTenant(factory, idText, CreateClient(factory, SubjectId, idText, AllPermissions));
    }

    /// <summary>Siembra la fila del tenant y todas sus llaves de módulo (incluida <c>scheduling</c>).</summary>
    public static async Task SeedTenantAsync(QepApiFactory factory, string tenantId, string displayName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var id = new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId));
        var now = DateTimeOffset.UtcNow;
        tenancy.Tenants.Add(Tenant.Create(
            id,
            $"scheduling-api-{tenantId[^2..]}",
            displayName,
            "es-CO",
            "America/Bogota",
            "yyyy-MM-dd",
            MembershipId.New(),
            now));
        await tenancy.SaveChangesAsync(Ct);

        foreach (var key in TenantModuleKeys.All)
        {
            await tenancy.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO tenancy.tenant_modules (tenant_id, module_key, enabled_at, source)
                VALUES ({id.Value}, {key.Value}, {now}, {TenantModuleSources.Seed})
                ON CONFLICT (tenant_id, module_key) DO NOTHING
                """,
                Ct);
        }
    }

    /// <summary>
    /// Una membresía activa del tenant con su usuario. Devuelve los dos ids: <c>MemberId</c> es lo que se vincula a un recurso y
    /// <c>UserId</c> es el sujeto (<c>X-Subject-Id</c>) con el que esa persona llama.
    /// </summary>
    public static async Task<(Guid MemberId, Guid UserId)> SeedMemberAsync(
        QepApiFactory factory, string tenantId, string email, string displayName, string role = "advisor")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var tenancy = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var identity = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var tenant = await tenancy.Tenants.FindAsync([new Modules.Tenancy.Domain.TenantId(Guid.Parse(tenantId))], Ct);
        var now = DateTimeOffset.UtcNow;

        // El correo es único en la base compartida de la clase: se le antepone un sufijo para que dos pruebas puedan sembrar «el mismo» miembro.
        var user = User.CreateInvited(UserId.New(), $"{Guid.CreateVersion7():N}-{email}", now);
        identity.Users.Add(user);
        var membership = Membership.CreateActive(MembershipId.New(), user.Id.Value, tenant!.Id, [role], "test", now);
        membership.UpdateProfile(displayName, null, now);
        tenancy.Memberships.Add(membership);
        await identity.SaveChangesAsync(Ct);
        await tenancy.SaveChangesAsync(Ct);
        return (membership.Id.Value, user.Id.Value);
    }

    // El stub concede sólo los defaults de tenancy cuando X-Permissions no está, así que los permisos de la Agenda se piden explícitos:
    // cada 403 queda atribuible al permiso o al tenant.
    public static HttpClient CreateClient(QepApiFactory factory, string subjectId, string tenantId, params string[] permissions)
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

    public static async Task<string> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(Ct);

    public static async Task<JsonElement> JsonOfAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await BodyAsync(response));
        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> CreatedAsync(HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body, Ct);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    public static async Task<JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url, Ct);
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK, await BodyAsync(response));
        return await JsonOfAsync(response);
    }

    public static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    public static string CodeOf(JsonElement problem) => problem.GetProperty("code").GetString()!;

    public static bool HasError(JsonElement problem, string key) =>
        problem.TryGetProperty("errors", out var errors) && errors.TryGetProperty(key, out _);

    /// <summary>
    /// Una cita vigente a futuro, sembrada directo en la base: el caso de uso de reserva llega en la Tarea 9 y estas pruebas sólo necesitan
    /// que exista una para que archivar su recurso o su servicio se rechace.
    /// </summary>
    public static async Task SeedFutureAppointmentAsync(
        QepApiFactory factory,
        string tenantId,
        Guid locationId,
        Guid resourceId,
        Guid serviceId,
        AppointmentStatus status = AppointmentStatus.Confirmed)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var tenant = Guid.Parse(tenantId);
        var now = DateTimeOffset.UtcNow;
        // Npgsql sólo escribe instantes con offset cero.
        var start = new DateTimeOffset(now.UtcDateTime.Date.AddDays(3).AddHours(14), TimeSpan.Zero);
        var end = start.AddMinutes(30);

        var session = Session.Create(
            SessionId.New(), tenant, new ResourceId(resourceId), new ServiceId(serviceId), start, end, 0, 1, false, now);
        session.Reserve(now);
        var appointment = Appointment.Create(
            AppointmentId.New(), tenant, new LocationId(locationId), new ResourceId(resourceId), new ServiceId(serviceId),
            Guid.CreateVersion7(), session.Id, null, start, end, AppointmentStatus.Confirmed, AppointmentSource.BackOffice, null, null, null, now);
        if (status == AppointmentStatus.Cancelled)
        {
            // Una cita nace Pending o Confirmed; una cancelada se llega a ser cancelándola, como en producción.
            appointment.Cancel(null, null, now);
            session.Release(now);
        }

        db.Sessions.Add(session);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);
    }

    public sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
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
            // Fijado, nunca heredado de appsettings.json: con "infobip" y las claves ausentes el validador de Notifications falla al arrancar.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            // Fijado: los user-secrets del developer pueden traer Seed:Enabled=true y sembrar datos en el tenant de pruebas.
            builder.UseSetting("Seed:Enabled", "false");
        }
    }
}

/// <summary>Una base para toda una clase de pruebas; cada prueba trabaja en su propio tenant, así que no se estorban.</summary>
public sealed class SchedulingApiWorld : IAsyncLifetime
{
    private PostgreSqlContainer? _database;

    internal SchedulingApiHarness.QepApiFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _database = await SchedulingApiHarness.StartDatabaseAsync();
        Factory = new SchedulingApiHarness.QepApiFactory(_database.GetConnectionString());
    }

    public async ValueTask DisposeAsync()
    {
        Factory?.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

/// <summary>Un tenant de prueba: <see cref="Admin"/> tiene los cinco permisos de la Agenda.</summary>
internal sealed class SchedulingTenant(SchedulingApiHarness.QepApiFactory factory, string idText, HttpClient admin) : IDisposable
{
    public SchedulingApiHarness.QepApiFactory Factory { get; } = factory;

    public string IdText { get; } = idText;

    public Guid Id { get; } = Guid.Parse(idText);

    public HttpClient Admin { get; } = admin;

    public string Url => SchedulingApiHarness.SchedulingUrl(IdText);

    /// <summary>Un cliente del mismo tenant con sólo los permisos pedidos y, si se da, otro sujeto.</summary>
    public HttpClient Client(string? subjectId, params string[] permissions) =>
        SchedulingApiHarness.CreateClient(Factory, subjectId ?? SchedulingApiHarness.SubjectId, IdText, permissions);

    public void Dispose() => Admin.Dispose();
}
