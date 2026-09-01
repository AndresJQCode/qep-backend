using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class MembershipLifecycleApiTests
{
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";

    [Fact]
    public async Task SuspendActiveMembershipTransitionsToSuspended()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);
        // Se invita como advisor y se promueve después por el mismo camino que
        // UpdateRolesChangesMembershipRoles: lo que se prueba acá es la suspensión de un
        // admin habiendo otro, no la invitación (que hoy admite cualquier rol del catálogo).
        // Se suspende al promovido y no al owner: la membresía de registro está protegida.
        var secondAdminId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        var promoted = await SendRolesAsync(ownerClient, tenantId, secondAdminId, AdminRoles);
        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        await ActivateMembershipAsync(factory.ConnectionString, secondAdminId);

        var response = await SendActionAsync(ownerClient, tenantId, secondAdminId, "suspend");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Suspended", membership!.State);
    }

    /// <summary>
    /// La membresía del owner (Origin de registro, ADR 0017) no se suspende, no se quita y
    /// no pierde el rol admin — por nadie, aunque haya otro admin activo. Sin esta guarda,
    /// `last_active_manager` deja de proteger al owner apenas se promueve un segundo admin.
    /// </summary>
    [Fact]
    public async Task SuspendOwnerMembershipIsRejectedEvenWithAnotherActiveAdmin()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);
        await AddActiveAdminAsync(factory, ownerClient, tenantId);

        var response = await SendActionAsync(ownerClient, tenantId, ownerMembershipId, "suspend");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.owner_protected", problem!.Code);
    }

    [Fact]
    public async Task RemoveOwnerMembershipIsRejectedEvenWithAnotherActiveAdmin()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);
        await AddActiveAdminAsync(factory, ownerClient, tenantId);

        var response = await SendActionAsync(ownerClient, tenantId, ownerMembershipId, "remove");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.owner_protected", problem!.Code);
    }

    [Fact]
    public async Task UpdateRolesCannotStripAdminFromOwnerMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);
        await AddActiveAdminAsync(factory, ownerClient, tenantId);

        var response = await SendRolesAsync(
            ownerClient, tenantId, ownerMembershipId, AdvisorRoles);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.owner_protected", problem!.Code);
    }

    [Fact]
    public async Task UpdateRolesOnOwnerKeepingAdminIsAllowed()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);

        var response = await SendRolesAsync(
            ownerClient, tenantId, ownerMembershipId, ["admin", "advisor"]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(["admin", "advisor"], membership!.Roles);
        Assert.True(membership.IsOwner);
    }

    [Fact]
    public async Task ListMarksOnlyTheOwnerMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);
        var invitedId = await InviteAsync(ownerClient, tenantId, NewEmail());

        var response = await ownerClient.GetAsync(
            $"/api/v1/tenants/{tenantId}/memberships",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<MembershipListPayload>(
            TestContext.Current.CancellationToken);
        var owner = Assert.Single(list!.Items, item => item.Id == ownerMembershipId);
        Assert.True(owner.IsOwner);
        var invited = Assert.Single(list.Items, item => item.Id == invitedId);
        Assert.False(invited.IsOwner);
    }

    [Fact]
    public async Task SuspendNonActiveMembershipIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var invitedId = await InviteAsync(ownerClient, tenantId, NewEmail());

        var response = await SendActionAsync(ownerClient, tenantId, invitedId, "suspend");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RemoveInvitedMembershipTransitionsToRemoved()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var invitedId = await InviteAsync(ownerClient, tenantId, NewEmail());

        var response = await SendActionAsync(ownerClient, tenantId, invitedId, "remove");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Removed", membership!.State);
    }

    [Fact]
    public async Task RemoveAlreadyRemovedMembershipIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var invitedId = await InviteAsync(ownerClient, tenantId, NewEmail());
        await SendActionAsync(ownerClient, tenantId, invitedId, "remove");

        var response = await SendActionAsync(ownerClient, tenantId, invitedId, "remove");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SuspendOwnMembershipIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, ownerUserId, _) =
            await RegisterTenantWithOwnerAsync(factory);
        using var selfClient = CreateClient(factory, ownerUserId.ToString(), tenantId);

        var response = await SendActionAsync(selfClient, tenantId, ownerMembershipId, "suspend");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RemoveOwnMembershipIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, ownerUserId, _) =
            await RegisterTenantWithOwnerAsync(factory);
        using var selfClient = CreateClient(factory, ownerUserId.ToString(), tenantId);

        var response = await SendActionAsync(selfClient, tenantId, ownerMembershipId, "remove");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task SuspendLastActiveManagerIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, _) =
            await RegisterTenantWithOwnerAsync(factory);
        using var sameTenantOtherSubject = CreateClient(
            factory, Guid.CreateVersion7().ToString(), tenantId);

        var response = await SendActionAsync(
            sameTenantOtherSubject, tenantId, ownerMembershipId, "suspend");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RemoveLastActiveManagerIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, _) =
            await RegisterTenantWithOwnerAsync(factory);
        using var sameTenantOtherSubject = CreateClient(
            factory, Guid.CreateVersion7().ToString(), tenantId);

        var response = await SendActionAsync(
            sameTenantOtherSubject, tenantId, ownerMembershipId, "remove");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task RemoveNonManagerMemberIsAllowedEvenAsOnlyOtherActiveMember()
    {
        // Prueba que la guarda es precisa a nivel manager, y no un chequeo genérico de "última
        // membresía activa": el owner (manager) sigue activo, así que quitar al único miembro
        // común tiene que funcionar aunque deje vacío el pool de activos no-manager.
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        await ActivateMembershipAsync(factory.ConnectionString, memberId);

        var response = await SendActionAsync(ownerClient, tenantId, memberId, "remove");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRolesChangesMembershipRoles()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendRolesAsync(ownerClient, tenantId, memberId, AdminRoles);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(AdminRoles, membership!.Roles);
        Assert.Equal(2, membership.Version);
    }

    [Fact]
    public async Task UpdateRolesForUnknownRoleIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendRolesAsync(ownerClient, tenantId, memberId, ["tenancy.unknown"]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRolesCannotRemoveLastActiveManager()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, ownerClient) =
            await RegisterTenantWithOwnerAsync(factory);

        var response = await SendRolesAsync(
            ownerClient, tenantId, ownerMembershipId, AdvisorRoles);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRolesRequiresIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendRolesAsync(
            ownerClient, tenantId, memberId, AdminRoles, expectedVersion: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task UpdateRolesWithStaleVersionIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendRolesAsync(
            ownerClient, tenantId, memberId, AdminRoles, expectedVersion: 99);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task ManageFromAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, _) = await RegisterTenantWithOwnerAsync(factory);
        using var otherClient = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await SendActionAsync(otherClient, tenantId, ownerMembershipId, "suspend");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ManageOfUnknownMembershipIsNotFound()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);

        var response = await SendActionAsync(
            ownerClient, tenantId, Guid.CreateVersion7(), "suspend");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static readonly string[] AdvisorRoles = ["advisor"];
    private static readonly string[] AdminRoles = ["admin"];

    private static string NewEmail() => $"member-{Guid.NewGuid():N}@example.com";

    private static string NewSlug() => $"org-{Guid.NewGuid():N}"[..12];

    // Registra un tenant (con signup público habilitado) para conseguir una Membership de owner
    // ya en estado Active (Membership.CreateActive, ADR 0016/0017) — la única forma de llegar a
    // Active sin la vuelta completa de login con Google. Devuelve un cliente acotado al tenant
    // nuevo por headers del stub de desarrollo (set de permisos por defecto = grant de owner).
    private static async Task<(string TenantId, Guid OwnerMembershipId, Guid OwnerUserId, HttpClient Client)>
        RegisterTenantWithOwnerAsync(QepApiFactory factory)
    {
        var ownerEmail = NewEmail();
        using (var bootstrapClient = CreateClient(
            factory, Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString()))
        {
            bootstrapClient.DefaultRequestHeaders.Add("X-Email", ownerEmail);
            bootstrapClient.DefaultRequestHeaders.Add("X-Email-Verified", "true");

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/v1/auth/register-tenant")
            {
                Content = JsonContent.Create(new
                {
                    displayName = "Acme Organization",
                    slug = NewSlug(),
                    defaultCulture = "es-CO",
                    timeZone = "America/Bogota",
                    dateFormat = "yyyy-MM-dd",
                }),
            };
            var response = await bootstrapClient.SendAsync(
                request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var registered = await response.Content.ReadFromJsonAsync<RegisterPayload>(
                TestContext.Current.CancellationToken);
            Assert.NotNull(registered);

            var client = CreateClient(
                factory,
                Guid.CreateVersion7().ToString(),
                registered!.TenantId.ToString());

            var ownerMembershipId = await FindMembershipIdAsync(
                factory.ConnectionString, registered.TenantId, registered.OwnerUserId);

            return (
                registered.TenantId.ToString(),
                ownerMembershipId,
                registered.OwnerUserId,
                client);
        }
    }

    private static async Task<Guid> InviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        IReadOnlyCollection<string>? roles = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, roles = roles ?? AdvisorRoles })
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        return membership!.Id;
    }

    // Deja al tenant con un segundo admin activo, para que la guarda que se ejercite sea la
    // del owner y no `last_active_manager`. Se invita directo como admin: la invitación hoy
    // admite cualquier rol del catálogo.
    private static async Task<Guid> AddActiveAdminAsync(
        QepApiFactory factory,
        HttpClient client,
        string tenantId)
    {
        var membershipId = await InviteAsync(client, tenantId, NewEmail(), AdminRoles);
        await ActivateMembershipAsync(factory.ConnectionString, membershipId);
        return membershipId;
    }

    private static async Task<HttpResponseMessage> SendActionAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId,
        string action)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/{action}");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> SendRolesAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId,
        IReadOnlyCollection<string> roles,
        long? expectedVersion = 1)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/roles")
        {
            Content = JsonContent.Create(new { roles })
        };
        if (expectedVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<Guid> FindMembershipIdAsync(
        string connectionString,
        Guid tenantId,
        Guid userId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT id FROM tenancy.memberships WHERE tenant_id = @tenantId AND user_id = @userId",
            connection);
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("userId", userId);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (Guid)result!;
    }

    // Saltea la vuelta de invitar-aceptar (que requiere un login real de Google) para que
    // las pruebas puedan poner una segunda membresía en estado Active directamente.
    private static async Task ActivateMembershipAsync(string connectionString, Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.memberships SET state = 'Active', accepted_at = now() WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", membershipId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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

    private static HttpClient CreateClient(
        QepApiFactory factory,
        string subjectId,
        string tenantId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Subject-Id", subjectId);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenantId);
        return client;
    }

    private sealed record RegisterPayload(Guid TenantId, Guid OwnerUserId);

    private sealed record MembershipListItemPayload(
        Guid Id,
        Guid UserId,
        string? Email,
        Guid TenantId,
        string State,
        IReadOnlyCollection<string> Roles,
        DateTimeOffset InvitedAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset ExpiresAt,
        long Version,
        bool IsOwner);

    private sealed record MembershipListPayload(
        IReadOnlyList<MembershipListItemPayload> Items);

    private sealed record ProblemPayload(string Code);

    private sealed class QepApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        public string ConnectionString => connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado, no heredado: appsettings.json lleva el proveedor con el que se despliega el
            // producto, y una suite de integración que depende de eso termina dependiendo de las
            // credenciales de quien la corra. Con "infobip" y las claves de Infobip ausentes —CI,
            // un clon nuevo— NotificationsOptionsValidator falla al arrancar y todas las pruebas
            // del archivo mueren antes de llegar a su aserción.
            // El canal de log es el default de desarrollo (SDD-CT-03). SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
        }
    }
}
