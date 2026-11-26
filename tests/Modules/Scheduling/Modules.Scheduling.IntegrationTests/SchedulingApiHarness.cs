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

    /// <summary>La fecha como la serializa la API (<c>yyyy-MM-dd</c>), sin depender de la cultura de quien corre la prueba.</summary>
    public static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

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
        var idText = $"01900000-0000-7000-8000-{Interlocked.Increment(ref _tenantCounter):x12}";
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
            $"scheduling-api-{tenantId[^6..]}",
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

    /// <summary>
    /// Las filas de <c>audit.entries</c> de una acción sobre un recurso del tenant: la auditoría atómica no tiene API de lectura acá.
    /// Verifica el efecto que importa (la fila existe con acción y entidad correctas), no sólo el status HTTP.
    /// </summary>
    public static async Task<int> CountAuditEntriesAsync(
        QepApiFactory factory, string tenantId, string action, string resourceType, Guid resourceId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var tenant = Guid.Parse(tenantId);
        var id = resourceId.ToString();
        return await db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value" FROM audit.entries
                WHERE tenant_id = {tenant} AND action = {action} AND resource_type = {resourceType} AND resource_id = {id}
                  AND source = 'scheduling' AND outcome = 'success'
                """)
            .SingleAsync(Ct);
    }

    /// <summary>The <c>changed_fields</c> column of the audit entries of one action on one resource, as raw JSON text, oldest first.</summary>
    public static async Task<IReadOnlyList<string>> ReadAuditFieldsAsync(
        QepApiFactory factory, string tenantId, string action, string resourceType, Guid resourceId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var tenant = Guid.Parse(tenantId);
        var id = resourceId.ToString();
        return await db.Database
            .SqlQuery<string>(
                $"""
                SELECT changed_fields::text AS "Value" FROM audit.entries
                WHERE tenant_id = {tenant} AND action = {action} AND resource_type = {resourceType} AND resource_id = {id}
                  AND source = 'scheduling'
                ORDER BY occurred_at
                """)
            .ToListAsync(Ct);
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

    public static async Task<Guid> MainLocationIdAsync(SchedulingTenant tenant)
    {
        var locations = await GetJsonAsync(tenant.Admin, $"{tenant.Url}/locations");
        return locations.EnumerateArray().Single(location => location.GetProperty("isMain").GetBoolean()).GetProperty("id").GetGuid();
    }

    public static async Task<Guid> CreateResourceAsync(SchedulingTenant tenant, string name, Guid? memberId = null, Guid? locationId = null) =>
        (await CreatedAsync(tenant.Admin, $"{tenant.Url}/resources", new { displayName = name, memberId, locationId })).GetProperty("id").GetGuid();

    public static async Task<Guid> CreateServiceAsync(SchedulingTenant tenant, string name = "Limpieza", int durationMinutes = 30) =>
        (await CreatedAsync(
            tenant.Admin,
            $"{tenant.Url}/services",
            new { name, durationMinutes, bufferMinutes = 0, bookableByChannel = true })).GetProperty("id").GetGuid();

    /// <summary>El cuerpo de una franja semanal: los lunes de 08:00 a 12:00 (minutos locales 480 a 720).</summary>
    public static object WeeklyBlockBody(
        IEnumerable<Guid> resourceIds,
        IEnumerable<Guid> serviceIds,
        string name = "Mañanas",
        int capacity = 1,
        string? sessionMode = null) =>
        new
        {
            name,
            kind = "Weekly",
            weekly = new[] { new { day = 1, ranges = new[] { new { @from = 480, to = 720 } } } },
            capacityPerResource = capacity,
            sessionMode,
            resourceIds,
            serviceIds,
        };

    /// <summary>El cuerpo de una franja puntual de 09:00 a 12:00 (minutos locales 540 a 720) en una fecha.</summary>
    public static object OneOffBlockBody(
        DateOnly date,
        IEnumerable<Guid> resourceIds,
        IEnumerable<Guid> serviceIds,
        string name = "Jornada extendida",
        int capacity = 1,
        string? sessionMode = null) =>
        new
        {
            name,
            kind = "OneOff",
            oneOff = new { date, ranges = new[] { new { @from = 540, to = 720 } } },
            capacityPerResource = capacity,
            sessionMode,
            resourceIds,
            serviceIds,
        };

    /// <summary>Un cliente que es el médico de un recurso: con lectura y franjas pero sin «ver todas las agendas».</summary>
    public static async Task<(Guid ResourceId, HttpClient Provider)> CreateProviderAsync(
        SchedulingTenant tenant, string email, string name)
    {
        var (memberId, userId) = await SeedMemberAsync(tenant.Factory, tenant.IdText, email, name);
        var resourceId = await CreateResourceAsync(tenant, $"Dr. {name}", memberId);
        var provider = tenant.Client(userId.ToString(), SchedulingPermissions.AppointmentRead, SchedulingPermissions.AvailabilityManage);
        return (resourceId, provider);
    }

    /// <summary>El primer lunes a más de dos semanas (los instantes de las pruebas no deben caer en el pasado).</summary>
    public static DateOnly FutureMonday()
    {
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14);
        while (day.DayOfWeek != DayOfWeek.Monday)
        {
            day = day.AddDays(1);
        }

        return day;
    }

    /// <summary>Las 09:00 locales (America/Bogota, UTC-5, sin horario de verano) de un día, como instante UTC.</summary>
    public static DateTimeOffset NineOClock(DateOnly day) =>
        new(day.ToDateTime(new TimeOnly(14, 0), DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>
    /// Una cita vigente sobre una sesión que ya existe (publicada por la API), sembrada directo en la base: reservar es de la Tarea 9.
    /// </summary>
    public static async Task SeedAppointmentOnSessionAsync(QepApiFactory factory, string tenantId, Guid locationId, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var session = await db.Sessions.SingleAsync(value => value.Id == new SessionId(sessionId), Ct);
        var now = DateTimeOffset.UtcNow;
        session.Reserve(now);
        db.Appointments.Add(Appointment.Create(
            AppointmentId.New(), Guid.Parse(tenantId), new LocationId(locationId), session.ResourceId, session.ServiceId,
            Guid.CreateVersion7(), session.Id, null, session.StartsAt, session.EndsAt, AppointmentStatus.Confirmed,
            AppointmentSource.BackOffice, null, null, null, now));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Una cita en el estado y la hora que se pida, sembrada directo en la base con su propia sesión de cupo 1: las lecturas del calendario y
    /// del escritorio necesitan citas ya vencidas, ya atendidas o pedidas hace horas, y la API de reserva no deja crearlas así. Se crea en
    /// <paramref name="requestedAt"/> (por defecto, una hora antes del inicio) y se marca con la hora de su fin, que es lo que haría quien
    /// la atiende después de la cita.
    /// </summary>
    public static async Task<Guid> SeedAppointmentAsync(
        QepApiFactory factory,
        string tenantId,
        Guid locationId,
        Guid resourceId,
        Guid serviceId,
        Guid customerId,
        DateTimeOffset start,
        AppointmentStatus status,
        DateTimeOffset? requestedAt = null,
        AppointmentSource source = AppointmentSource.BackOffice,
        int minutes = 30)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var tenant = Guid.Parse(tenantId);
        var end = start.AddMinutes(minutes);
        var created = requestedAt ?? start.AddHours(-1);

        var session = Session.Create(
            SessionId.New(), tenant, new ResourceId(resourceId), new ServiceId(serviceId), start, end, 0, 1, false, created);
        session.Reserve(created);
        var appointment = Appointment.Create(
            AppointmentId.New(), tenant, new LocationId(locationId), new ResourceId(resourceId), new ServiceId(serviceId),
            customerId, session.Id, null, start, end,
            status == AppointmentStatus.Pending ? AppointmentStatus.Pending : AppointmentStatus.Confirmed,
            source, null, null, null, created);
        switch (status)
        {
            case AppointmentStatus.Completed:
                appointment.MarkAttended(null, end);
                break;
            case AppointmentStatus.NoShow:
                appointment.MarkNoShow(null, end);
                break;
            case AppointmentStatus.Cancelled:
                appointment.Cancel(null, null, created);
                session.Release(created);
                break;
        }

        db.Sessions.Add(session);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);
        return appointment.Id.Value;
    }

    /// <summary>
    /// A cancelled appointment on a session, as the booking flow leaves it: the appointment was cancelled and its seat released, so the
    /// session has history (the foreign key forbids deleting it) but no active booking.
    /// </summary>
    public static async Task SeedCancelledAppointmentOnSessionAsync(QepApiFactory factory, string tenantId, Guid locationId, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var session = await db.Sessions.SingleAsync(value => value.Id == new SessionId(sessionId), Ct);
        var now = DateTimeOffset.UtcNow;
        session.Reserve(now);
        var appointment = Appointment.Create(
            AppointmentId.New(), Guid.Parse(tenantId), new LocationId(locationId), session.ResourceId, session.ServiceId,
            Guid.CreateVersion7(), session.Id, null, session.StartsAt, session.EndsAt, AppointmentStatus.Confirmed,
            AppointmentSource.BackOffice, null, null, null, now);
        appointment.Cancel(null, null, now);
        session.Release(now);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The session row as the database holds it, deactivated or not (the API only returns the ones it created or republished).</summary>
    public static async Task<(bool Exists, bool IsActive, bool IsPublished, int Capacity)> ReadSessionAsync(QepApiFactory factory, Guid sessionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(value => value.Id == new SessionId(sessionId), Ct);
        return session is null ? (false, false, false, 0) : (true, session.IsActive, session.IsPublished, session.Capacity);
    }

    /// <summary>Un cliente del tenant dado de alta por la propia Agenda (<c>POST customers</c>), que es la vía que usa la pantalla de la cita.</summary>
    public static async Task<Guid> CreateCustomerAsync(SchedulingTenant tenant, string name = "Ana Ruiz", string? phone = "3001234567") =>
        (await CreatedAsync(tenant.Admin, $"{tenant.Url}/customers", new { name, phone })).GetProperty("id").GetGuid();

    public static object BookBody(
        Guid serviceId,
        DateTimeOffset startsAt,
        Guid customerId,
        Guid? resourceId = null,
        string? source = null,
        string? clientRequestId = null,
        string? note = null) =>
        new { serviceId, resourceId, startsAt, customerId, source, clientRequestId, note };

    public static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object? body, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(request, Ct);
    }

    /// <summary>La <c>version</c> de una respuesta, como la espera <c>If-Match</c>.</summary>
    public static string VersionOf(JsonElement body) => body.GetProperty("version").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Las citas y las filas de historial que la base guarda para un recurso, de cualquier estado.</summary>
    public static async Task<(int Appointments, int Events)> CountAppointmentsAsync(QepApiFactory factory, Guid resourceId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var resource = new ResourceId(resourceId);
        var appointments = await db.Appointments.AsNoTracking().Where(value => value.ResourceId == resource).Include(value => value.Events).ToListAsync(Ct);
        return (appointments.Count, appointments.Sum(value => value.Events.Count));
    }

    /// <summary>Las sesiones de un recurso tal como las guarda la base, con su cupo, sus reservas y si están activas.</summary>
    public static async Task<IReadOnlyList<(DateTimeOffset StartsAt, int Capacity, int Booked, bool IsActive, bool IsPublished)>> ReadSessionsAsync(
        QepApiFactory factory, Guid resourceId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var resource = new ResourceId(resourceId);
        var sessions = await db.Sessions.AsNoTracking().Where(value => value.ResourceId == resource).OrderBy(value => value.StartsAt).ToListAsync(Ct);
        return [.. sessions.Select(value => (value.StartsAt, value.Capacity, value.BookedCount, value.IsActive, value.IsPublished))];
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
