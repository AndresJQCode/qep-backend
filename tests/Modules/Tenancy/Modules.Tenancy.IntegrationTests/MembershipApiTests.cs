using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Tenancy.IntegrationTests;

public sealed class MembershipApiTests
{
    private const string TenantId = "01900000-0000-7000-8000-000000000001";
    private const string SubjectId = "01900000-0000-7000-8000-000000000002";
    private const string OtherTenantId = "01900000-0000-7000-8000-0000000000ff";
    private const string OtherSubjectId = "01900000-0000-7000-8000-0000000000fe";
    private static readonly string[] DefaultRoles = ["advisor"];
    private static readonly string[] UnknownRoles = ["tenancy.unknown"];
    private static readonly string[] AdminRoles = ["admin"];
    private static readonly string[] BillingRoles = ["billing"];

    [Fact]
    public async Task InviteProvisionsUserMembershipAuditAndOutboxEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var response = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);
        Assert.Equal("Invited", membership!.State);
        Assert.Equal(email, membership.Email);
        Assert.NotEqual(Guid.Empty, membership.UserId);
        // Ventana de invitación de 72 horas, según el ADR 0016.
        Assert.True(membership.ExpiresAt > membership.InvitedAt.AddHours(71));
        Assert.True(membership.ExpiresAt <= membership.InvitedAt.AddHours(72));

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // Identity aprovisionó un usuario invitado para el email.
        var user = await QueryRowAsync(
            connection,
            "SELECT status FROM identity.users WHERE email = @email",
            ("email", email));
        Assert.NotNull(user);
        Assert.Equal("Invited", user![0]);

        // Tenancy creó la membresía en estado Invited.
        var membershipRow = await QueryRowAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE id = @id",
            ("id", membership.Id));
        Assert.NotNull(membershipRow);
        Assert.Equal("Invited", membershipRow![0]);

        // La entrada de auditoría registra la invitación.
        var audit = await QueryRowAsync(
            connection,
            """
            SELECT actor_id::text, outcome
            FROM audit.entries
            WHERE resource_id = @resourceId AND action = 'tenancy.membership.invited'
            """,
            ("resourceId", membership.Id.ToString()));
        Assert.NotNull(audit);
        Assert.Equal(SubjectId, audit![0]);
        Assert.Equal("success", audit[1]);

        // El outbox lleva el evento de integración correlacionado, en la misma unidad de trabajo.
        var outbox = await QueryRowAsync(
            connection,
            """
            SELECT correlation_id, payload::text
            FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-invited.v1'
              AND payload::text LIKE @idPattern
            """,
            ("idPattern", $"%{membership.Id}%"));
        Assert.NotNull(outbox);
        Assert.False(string.IsNullOrWhiteSpace(outbox![0]));
    }

    [Fact]
    public async Task InviteToAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Autenticado como OtherTenant, intentando invitar al tenant sembrado.
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await InviteAsync(client, TenantId, NewEmail());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task InvitingSameEmailTwiceIsIdempotent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstMembership = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        var secondMembership = await second.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(firstMembership);
        Assert.NotNull(secondMembership);
        Assert.Equal(firstMembership!.Id, secondMembership!.Id);
        Assert.Equal(firstMembership.UserId, secondMembership.UserId);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var count = await ScalarAsync(
            connection,
            "SELECT count(*) FROM tenancy.memberships WHERE user_id = @userId",
            ("userId", firstMembership.UserId));
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task ListReturnsInvitedMembersForTheirTenantOnly()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();
        var invited = await InviteAsync(client, TenantId, email);
        var invitedMembership = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/memberships",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var memberships = await response.Content.ReadFromJsonAsync<MembershipListItemPayload[]>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(memberships);
        var listed = Assert.Single(
            memberships!,
            membership => membership.Id == invitedMembership!.Id);
        Assert.Equal(email, listed.Email);
        Assert.All(memberships!, membership => Assert.Equal(TenantId, membership.TenantId.ToString()));
    }

    /// <summary>
    /// El selector de asesores de cotizaciones filtra por rol en el servidor
    /// (`ListMembershipsQuery.Role`), no descartando filas del lado del cliente — facturación
    /// nunca puede ser "el asesor" de una cotización.
    /// </summary>
    [Fact]
    public async Task ListFiltersByRoleWhenRequested()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var advisorEmail = NewEmail();
        var billingEmail = NewEmail();
        await InviteAsync(client, TenantId, advisorEmail, DefaultRoles);
        await InviteAsync(client, TenantId, billingEmail, BillingRoles);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/memberships?role=advisor",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var memberships = await response.Content.ReadFromJsonAsync<MembershipListItemPayload[]>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(memberships);
        Assert.Contains(memberships!, membership => membership.Email == advisorEmail);
        Assert.DoesNotContain(memberships!, membership => membership.Email == billingEmail);
        Assert.All(memberships!, membership => Assert.Contains("advisor", membership.Roles));
    }

    [Fact]
    public async Task ListForAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/memberships",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthorizationCatalogReturnsRolesAndPermissions()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await client.GetAsync(
            $"/api/v1/tenants/{TenantId}/authorization/catalog",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalog = await response.Content.ReadFromJsonAsync<AuthorizationCatalogPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(catalog);
        Assert.False(string.IsNullOrWhiteSpace(catalog!.CatalogVersion));
        Assert.Contains(catalog!.Roles, role => role.Role == "admin");
        Assert.Contains(catalog.Roles, role =>
            role.Role == "admin" && role.RiskLevel == "high");
        Assert.Contains(catalog.Roles, role => role.Role == "advisor");
        Assert.Contains(catalog.Roles, role => role.Role == "billing");
        Assert.DoesNotContain(catalog.Roles, role => role.Role == "tenancy.owner");
        Assert.DoesNotContain(catalog.Roles, role => role.Role == "tenancy.member");
        Assert.Contains(
            catalog.Permissions,
            permission =>
                permission.Permission == "advisorship.manage" &&
                permission.DisplayName == "Gestionar miembros y roles");
    }

    [Fact]
    public async Task InviteWithUnknownRoleIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{TenantId}/memberships")
        {
            Content = JsonContent.Create(new
            {
                email = NewEmail(),
                roles = UnknownRoles
            })
        };
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    /// <summary>
    /// El admin no se asigna por invitación por correo — sólo al crear el tenant o por
    /// traspaso explícito (`UpdateMemberRoles`). Allowlist en `InviteMember.EnsureInvitableRoles`,
    /// no un blocklist de `admin`: cualquier rol que no sea `advisor`/`billing` cae acá.
    /// </summary>
    [Fact]
    public async Task InviteWithAdminRoleIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await InviteAsync(client, TenantId, NewEmail(), AdminRoles);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.role_not_invitable", problem?.Code);
    }

    [Fact]
    public async Task InviteWithBillingRoleSucceeds()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var response = await InviteAsync(client, TenantId, NewEmail(), BillingRoles);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// AUTH-05 / SDD-OD-04. El vencimiento es perezoso: sólo un intento de login pasa una
    /// invitación a Expired. Quien nunca lo intenta queda en Invited con un ExpiresAt pasado,
    /// y volver a invitarla devolvía esa fila muerta sin cambios — ni invitación nueva, ni
    /// error, ni aviso, y sin forma de que esa persona llegue a entrar nunca.
    ///
    /// La ventana se fuerza al pasado en la base y no moviendo un reloj: la API resuelve el
    /// tiempo por el IClock real, y lo que está bajo prueba es qué hace el handler con una
    /// fila cuya ventana ya venció.
    /// </summary>
    [Fact]
    public async Task ReinvitingALapsedInvitationIssuesAFreshOne()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);

        await LapseInvitationAsync(database, original!.Id);

        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var renewed = await second.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(renewed);
        Assert.Equal("Invited", renewed!.State);
        Assert.True(
            renewed.ExpiresAt > DateTimeOffset.UtcNow,
            "A renewed invitation must expire in the future.");
        Assert.True(renewed.Version > original.Version);

        // Renovada en el lugar: (user_id, tenant_id) es UNIQUE, así que una segunda fila es imposible.
        // Ver SDD-CT-15.
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM tenancy.memberships WHERE user_id = @userId AND tenant_id = @tenantId",
            ("userId", original.UserId),
            ("tenantId", Guid.Parse(TenantId)));
        Assert.Equal(1, rows);
        Assert.Equal(original.Id, renewed.Id);
    }

    /// <summary>
    /// La renovación tiene que llegarle a la persona: InvitationDeliveryWorker manda el email a
    /// partir del evento de outbox, así que una renovación que persiste sin volver a emitirlo
    /// es un no-op silencioso desde el punto de vista del invitado.
    /// </summary>
    [Fact]
    public async Task ReinvitingALapsedInvitationEmitsTheInvitedEventAgain()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);
        await LapseInvitationAsync(database, original!.Id);

        var second = await InviteAsync(client, TenantId, email);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var events = await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-invited.v1'
              AND payload::text LIKE '%' || @membershipId || '%'
            """,
            ("membershipId", original.Id.ToString()));
        Assert.Equal(2, events);

        var audit = await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM audit.entries
            WHERE action = 'tenancy.membership.invited' AND resource_id = @membershipId
            """,
            ("membershipId", original.Id.ToString()));
        Assert.Equal(2, audit);
    }

    /// <summary>
    /// CA-AUTH-05-12: una invitación viva queda intacta. Renovarla invalidaría el link que ya
    /// está en la bandeja de alguien y movería el plazo en silencio.
    /// </summary>
    [Fact]
    public async Task ReinvitingALiveInvitationRemainsAnIdempotentNoOp()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);

        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var repeated = await second.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(repeated);
        Assert.Equal(original!.Id, repeated!.Id);
        Assert.Equal(original.Version, repeated.Version);
        // Comparado al microsegundo: la primera respuesta lleva el valor en memoria y la
        // segunda vuelve de Postgres, que guarda microsegundos. Una comparación más estricta
        // falla por ticks de menos de un microsegundo y no prueba nada sobre el no-op.
        Assert.True(
            (repeated.ExpiresAt - original.ExpiresAt).Duration() < TimeSpan.FromMilliseconds(1),
            "A no-op must not move the expiry window.");
    }

    /// <summary>
    /// CA-AUTH-05-12 por el camino real. La prueba unitaria de una membresía Active llama a
    /// Reinvite() directo, cosa que el handler nunca hace para ese estado — corta antes. Sin
    /// esto, el criterio parecía cubierto y el comportamiento de producción quedaba sin
    /// probar. Lo encontró la revisión de AUTH-05.
    /// </summary>
    [Fact]
    public async Task ReinvitingAnActiveMemberIsAnIdempotentNoOp()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);

        // Activada en la base y no entrando por login: la aceptación es un efecto lateral
        // de un login real, y esta prueba es sobre qué le hace una segunda invitación a un
        // miembro que ya está activo.
        await SetStateAsync(database, original!.Id, "Active");

        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var repeated = await second.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(repeated);
        Assert.Equal(original.Id, repeated!.Id);
        Assert.Equal("Active", repeated.State);
        Assert.Equal(original.Version, repeated.Version);
    }

    /// <summary>
    /// Una membresía que un administrador suspendió no se reabre invitando de nuevo a la
    /// persona. Antes de AUTH-05 este camino insertaba una segunda fila y moría en el índice
    /// UNIQUE (user_id, tenant_id), saliendo como 500; ahora es un 422 declarado.
    /// Si re-invitar *debería* restaurarla es SDD-OD-13, una decisión de producto.
    /// </summary>
    [Fact]
    public async Task ReinvitingASuspendedMemberIsRefusedWithoutRestoringThem()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var first = await InviteAsync(client, TenantId, email);
        var original = await first.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(original);
        await SetStateAsync(database, original!.Id, "Suspended");

        var second = await InviteAsync(client, TenantId, email);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var state = await QueryRowAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE id = @id",
            ("id", original.Id));
        Assert.NotNull(state);
        Assert.Equal("Suspended", state![0]);
    }

    /// <summary>
    /// AUTH-11. Hasta este slice una suspensión era un callejón sin salida: `Reinvite`
    /// rechaza `Suspended` y no había operación que la moviera, así que alguien suspendido
    /// por error no tenía forma de volver desde el producto.
    /// </summary>
    [Fact]
    public async Task ReactivateReturnsASuspendedMemberToActive()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);
        var email = NewEmail();

        var invited = await InviteAsync(client, TenantId, email);
        var membership = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);
        await SetStateAsync(database, membership!.Id, "Suspended");

        var response = await ReactivateAsync(client, TenantId, membership.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reactivated = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(reactivated);
        Assert.Equal("Active", reactivated!.State);
        Assert.True(reactivated.Version > membership.Version);
    }

    /// <summary>
    /// El estado persistido, la auditoría y el evento de outbox, no sólo el status HTTP:
    /// `AGENTS.md` §7b lo exige, y una reactivación sin rastro es justamente lo que la
    /// operación separada venía a evitar.
    /// </summary>
    [Fact]
    public async Task ReactivateWritesAuditEntryAndOutboxEvent()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var invited = await InviteAsync(client, TenantId, NewEmail());
        var membership = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);
        await SetStateAsync(database, membership!.Id, "Suspended");

        var response = await ReactivateAsync(client, TenantId, membership.Id);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var state = await QueryRowAsync(
            connection,
            "SELECT state FROM tenancy.memberships WHERE id = @id",
            ("id", membership.Id));
        Assert.NotNull(state);
        Assert.Equal("Active", state![0]);

        var audit = await QueryRowAsync(
            connection,
            """
            SELECT actor_id::text, outcome FROM audit.entries
            WHERE action = 'tenancy.membership.reactivated' AND resource_id = @membershipId
            """,
            ("membershipId", membership.Id.ToString()));
        Assert.NotNull(audit);
        Assert.Equal(SubjectId, audit![0]);
        Assert.Equal("success", audit[1]);

        var outbox = await ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM platform.outbox_messages
            WHERE event_name = 'tenancy.membership-reactivated.v1'
              AND payload::text LIKE '%' || @membershipId || '%'
            """,
            ("membershipId", membership.Id.ToString()));
        Assert.Equal(1, outbox);
    }

    [Fact]
    public async Task ReactivateRejectsAMemberThatIsNotSuspended()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var invited = await InviteAsync(client, TenantId, NewEmail());
        var membership = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);

        // Sigue en Invited: nunca fue suspendida.
        var response = await ReactivateAsync(client, TenantId, membership!.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ReactivateRequiresTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        using var client = CreateClient(factory, SubjectId, TenantId);

        var invited = await InviteAsync(client, TenantId, NewEmail());
        var membership = await invited.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);
        await SetStateAsync(database, membership!.Id, "Suspended");

        using var readerOnly = CreateClient(factory, SubjectId, TenantId);
        readerOnly.DefaultRequestHeaders.Add("X-Permissions", "advisorship.read");

        var response = await ReactivateAsync(readerOnly, TenantId, membership.Id);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> ReactivateAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/reactivate");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task SetStateAsync(
        PostgreSqlContainer database,
        Guid membershipId,
        string state)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.memberships SET state = @state WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("id", membershipId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task LapseInvitationAsync(
        PostgreSqlContainer database,
        Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE tenancy.memberships SET expires_at = @expiresAt WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("expiresAt", DateTimeOffset.UtcNow.AddHours(-1));
        command.Parameters.AddWithValue("id", membershipId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string NewEmail() => $"invitee-{Guid.NewGuid():N}@example.com";

    private static async Task<HttpResponseMessage> InviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        string[]? roles = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, roles = roles ?? DefaultRoles })
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string[]?> QueryRowAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            return null;
        }

        var values = new string[reader.FieldCount];
        for (var index = 0; index < reader.FieldCount; index++)
        {
            values[index] = reader.GetValue(index).ToString() ?? string.Empty;
        }

        return values;
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
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

    private sealed record ProblemPayload(string Code);

    private sealed record MembershipPayload(
        Guid Id,
        Guid UserId,
        string Email,
        Guid TenantId,
        string State,
        IReadOnlyCollection<string> Roles,
        DateTimeOffset InvitedAt,
        DateTimeOffset? AcceptedAt,
        DateTimeOffset ExpiresAt,
        long Version);

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
        long Version);

    private sealed record AuthorizationCatalogPayload(
        string CatalogVersion,
        IReadOnlyCollection<RoleCatalogItemPayload> Roles,
        IReadOnlyCollection<PermissionCatalogItemPayload> Permissions);

    private sealed record RoleCatalogItemPayload(
        string Role,
        string DisplayName,
        string Description,
        string Category,
        string RiskLevel,
        IReadOnlyCollection<string> Permissions);

    private sealed record PermissionCatalogItemPayload(
        string Permission,
        string DisplayName,
        string Description,
        string Category,
        string RiskLevel);

    private sealed class QepApiFactory(string connectionString)
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
            // Fijado, no heredado: appsettings.json lleva el proveedor con el que se despliega el
            // producto, y una suite de integración que depende de eso termina dependiendo de las
            // credenciales de quien la corra. Con "infobip" y las claves de Infobip ausentes —CI,
            // un clon nuevo— NotificationsOptionsValidator falla al arrancar y todas las pruebas
            // del archivo mueren antes de llegar a su aserción.
            // El canal de log es el default de desarrollo (SDD-CT-03). SDD-CT-17.
            builder.UseSetting("Notifications:EmailProvider", "log");
        }
    }
}
