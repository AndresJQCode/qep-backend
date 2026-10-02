using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modules.Quotations.Domain;
using Modules.Quotations.Infrastructure.Persistence;
using Modules.Storage.Domain;
using Modules.Storage.Infrastructure.Persistence;
using Modules.Tenancy.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Borrado físico del usuario huérfano. Cuando una membresía se quita
/// (<c>POST /memberships/{id}/remove</c>) y la persona no deja huella en ningún otro módulo,
/// Identity elimina la fila de <c>identity.users</c> de forma asíncrona, consumiendo
/// <c>tenancy.membership-removed.v1</c> del Outbox de plataforma con su propio inbox.
///
/// El borrado nunca pasa en el handler de quitar: <c>RemoveMemberHandler</c> lee el correo
/// después del commit para armar la respuesta. Por eso todas las pruebas esperan al worker.
///
/// Las huellas que retienen al usuario las declara cada módulo por <c>IUserReferenceProbe</c>:
/// una membresía viva en otro tenant (Tenancy), una cotización/pedido que referencia alguna de
/// sus membresías (Quotations) o un archivo del que es dueño (Storage). Auditoría y
/// notificaciones no retienen: son append-only y guardan snapshot.
///
/// Cuando nada lo retiene, antes de borrarlo se purgan sus membresías quitadas o vencidas
/// (<c>IUserReferencePurger</c>, spec 2026-10-02), lo que libera su código de asesor.
/// </summary>
public sealed class OrphanUserCleanupTests
{
    private const string Consumer = "identity.orphan-user-cleanup";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(15);
    private static readonly string[] AdvisorRoles = ["advisor"];

    [Fact]
    public async Task RemovingTheOnlyMembershipDeletesTheUserAndItsSessions()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedSessionAsync(connectionString, member.UserId);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountUsersAsync(connection, member.UserId) == 0);

        Assert.Equal(0, await CountSessionsAsync(connection, member.UserId));
        Assert.Equal(1, await CountInboxAsync(connection, member.Id));
        Assert.Equal(1, await CountAuditAsync(connection, member.UserId, "identity.user.deleted"));
    }

    /// <summary>
    /// Spec 2026-10-02: cuando el usuario se borra por no tener historia, su membresía quitada se
    /// borra con él y su código de asesor queda libre. Antes la fila quedaba apuntando a un usuario
    /// inexistente y bloqueaba el código para siempre (D4).
    /// </summary>
    [Fact]
    public async Task RemovingAMemberWithoutHistoryPurgesItsMembershipAndFreesItsAdvisorCode()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail(), advisorCode: 7);
        await ActivateMembershipAsync(connectionString, member.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () =>
            await CountUsersAsync(connection, member.UserId) == 0 &&
            await CountMembershipsAsync(connection, member.UserId) == 0);

        Assert.Equal(1, await CountAuditAsync(connection, member.Id, "tenancy.membership.purged"));
        var other = await SendInviteAsync(ownerClient, tenantId, NewEmail(), advisorCode: 7);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    /// <summary>
    /// D4 sigue en pie para quien tiene historia: la cotización retiene al usuario, así que ni él
    /// ni su membresía quitada se borran, y el código sigue ocupado.
    /// </summary>
    [Fact]
    public async Task AMemberWithHistoryKeepsItsRemovedMembershipAndItsAdvisorCode()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail(), advisorCode: 7);
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedQuotationAsync(factory, Guid.Parse(tenantId), advisorMembershipId: member.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
        Assert.Equal(1, await CountMembershipsAsync(connection, member.UserId));
        Assert.Equal(0, await CountAuditAsync(connection, member.Id, "tenancy.membership.purged"));
        var other = await SendInviteAsync(ownerClient, tenantId, NewEmail(), advisorCode: 7);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
        var problem = await other.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.advisor_code_taken", problem!.Code);
    }

    /// <summary>
    /// La misma persona vuelve después de la purga: como su usuario ya no existe, la invitación
    /// crea un usuario y una membresía nuevos, y esa membresía puede tomar su código de antes.
    /// </summary>
    [Fact]
    public async Task ReinvitingTheSameEmailAfterThePurgeTakesItsOldAdvisorCodeBack()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var email = NewEmail();
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, email, advisorCode: 7);
        await ActivateMembershipAsync(connectionString, member.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () =>
            await CountUsersAsync(connection, member.UserId) == 0 &&
            await CountMembershipsAsync(connection, member.UserId) == 0);

        var returning = await InviteAsync(ownerClient, tenantId, email, advisorCode: 7);
        Assert.NotEqual(member.Id, returning.Id);
        Assert.NotEqual(member.UserId, returning.UserId);
        Assert.Equal("Invited", returning.State);
        Assert.Equal(7, returning.AdvisorCode);
    }

    [Fact]
    public async Task AnActiveMembershipInAnotherTenantKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var email = NewEmail();
        var (firstTenant, firstOwner) = await RegisterTenantWithOwnerAsync(factory);
        var (secondTenant, secondOwner) = await RegisterTenantWithOwnerAsync(factory);
        var first = await InviteAsync(firstOwner, firstTenant, email);
        var second = await InviteAsync(secondOwner, secondTenant, email);
        Assert.Equal(first.UserId, second.UserId);
        await ActivateMembershipAsync(connectionString, first.Id);
        await ActivateMembershipAsync(connectionString, second.Id);

        var removal = await RemoveAsync(firstOwner, firstTenant, first.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, first.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, first.UserId));
    }

    [Fact]
    public async Task ASuspendedMembershipElsewhereKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var email = NewEmail();
        var (firstTenant, firstOwner) = await RegisterTenantWithOwnerAsync(factory);
        var (secondTenant, secondOwner) = await RegisterTenantWithOwnerAsync(factory);
        var first = await InviteAsync(firstOwner, firstTenant, email);
        var second = await InviteAsync(secondOwner, secondTenant, email);
        await ActivateMembershipAsync(connectionString, first.Id);
        await SetMembershipStateAsync(connectionString, second.Id, "Suspended");

        var removal = await RemoveAsync(firstOwner, firstTenant, first.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, first.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, first.UserId));
    }

    [Fact]
    public async Task BeingTheAdvisorOfAQuotationKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedQuotationAsync(factory, Guid.Parse(tenantId), advisorMembershipId: member.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
    }

    // El aprobador no es el asesor: si compartieran membresía, la prueba no distinguiría si lo
    // que retiene al usuario es advisor_id (ya cubierto arriba) o approved_by.
    [Fact]
    public async Task ApprovingAnOrderKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var advisor = await InviteAsync(ownerClient, tenantId, NewEmail());
        var approver = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, advisor.Id);
        await ActivateMembershipAsync(connectionString, approver.Id);
        await SeedOrderAsync(
            factory,
            Guid.Parse(tenantId),
            convertedByMembershipId: advisor.Id,
            approvedByMembershipId: approver.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, approver.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, approver.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, approver.UserId));
    }

    // Misma razón que ApprovingAnOrderKeepsTheUser: el canceller es una tercera membresía, no el
    // asesor ni el aprobador.
    [Fact]
    public async Task CancellingAnOrderKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var advisor = await InviteAsync(ownerClient, tenantId, NewEmail());
        var canceller = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, advisor.Id);
        await ActivateMembershipAsync(connectionString, canceller.Id);
        await SeedOrderAsync(
            factory,
            Guid.Parse(tenantId),
            convertedByMembershipId: advisor.Id,
            cancelledByMembershipId: canceller.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, canceller.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, canceller.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, canceller.UserId));
    }

    [Fact]
    public async Task OwningAFileKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedFileAsync(factory, Guid.Parse(tenantId), ownerUserId: member.UserId);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
    }

    // Spec 2026-09-16, D18: el frontend sube los comprobantes con ownerId = el usuario. Pasarlos a
    // PaymentProof no puede dejar de retener a quien los subió.
    [Fact]
    public async Task OwningAPaymentProofKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedFileAsync(
            factory, Guid.Parse(tenantId), ownerUserId: member.UserId, FileOwnerType.PaymentProof);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
    }

    /// <summary>
    /// Reentrega: se borra la fila del inbox para que el worker reclame el mensaje otra vez.
    /// El usuario ya no existe, así que la segunda pasada no tiene nada que borrar y sólo
    /// vuelve a marcar el inbox — sin excepción y sin frenar el loop.
    /// </summary>
    [Fact]
    public async Task ReprocessingTheSameMessageIsHarmless()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountUsersAsync(connection, member.UserId) == 0);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        await DeleteInboxAsync(connection, member.Id);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);

        Assert.Equal(0, await CountUsersAsync(connection, member.UserId));
        Assert.Equal(1, await CountAuditAsync(connection, member.UserId, "identity.user.deleted"));
    }

    /// <summary>
    /// Carrera con una invitación concurrente: sin serialización, Tenancy responde "sin huella",
    /// la invitación inserta su membresía y el DELETE la deja apuntando a un usuario borrado.
    /// Se reproduce sosteniendo desde afuera el advisory lock de <c>UserLifecycleLockKey</c>,
    /// como haría InviteMemberHandler: el worker tiene que esperar, y al entrar ya ve la
    /// membresía nueva.
    /// </summary>
    [Fact]
    public async Task AnInviteCommittedWhileTheLockIsHeldKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var email = NewEmail();
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var (secondTenant, _) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, email);
        await ActivateMembershipAsync(connectionString, member.Id);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await ExecuteAsync(
            connection,
            "SELECT pg_advisory_xact_lock(hashtext(@key))",
            ("key", UserLifecycleLockKey.For(email)));

        var removal = await RemoveAsync(ownerClient, tenantId, member.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        // El worker corre cada 3 s: a los 8 s ya reclamó el mensaje y tiene que estar bloqueado
        // en el lock, sin haber borrado ni marcado nada. Sin lock, acá el usuario ya no existe.
        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
        Assert.Equal(0, await CountInboxAsync(connection, member.Id));

        // La membresía nueva se commitea junto con el lock, como en el handler real.
        await ExecuteAsync(
            connection,
            """
            INSERT INTO tenancy.memberships
                (id, user_id, tenant_id, state, roles, origin, invited_at, accepted_at,
                 expires_at, version, created_at, updated_at)
            SELECT @id, user_id, @tenantId, 'Invited', roles, origin, now(), NULL,
                   now() + interval '7 days', version, now(), now()
            FROM tenancy.memberships WHERE id = @sourceId
            """,
            ("id", Guid.CreateVersion7()),
            ("tenantId", Guid.Parse(secondTenant)),
            ("sourceId", member.Id));
        await transaction.CommitAsync(TestContext.Current.CancellationToken);

        await WaitUntilAsync(async () => await CountInboxAsync(connection, member.Id) == 1);
        Assert.Equal(1, await CountUsersAsync(connection, member.UserId));
        Assert.Equal(0, await CountAuditAsync(connection, member.UserId, "identity.user.deleted"));
    }

    /// <summary>
    /// Un mensaje que falla no puede dejarle estado rastreado al siguiente. Un purgador de prueba,
    /// registrado antes que el de Tenancy, marca como borrada la membresía de A en el
    /// <c>TenancyDbContext</c> de su scope —sin guardar— y lanza. Si el scope fuera uno por lote,
    /// el <c>SaveChanges</c> de la purga de B se llevaría también la fila de A, aunque A nunca se
    /// borró. Como A falla siempre, en cada tick su mensaje va primero que el de B en el mismo
    /// lote, así que el resultado no depende de en qué tick caigan las dos bajas.
    /// </summary>
    [Fact]
    public async Task AFailedMessageDoesNotLeakTrackedChangesIntoTheNextOne()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var fault = new PurgeFault();
        using var factory = new QepApiFactory(connectionString, services =>
        {
            services.AddSingleton(fault);
            // Primero en la lista: GetServices devuelve en orden de registro, y tiene que correr
            // antes que MembershipUserReferencePurger, que si no commitearía la purga de A.
            services.Insert(0, ServiceDescriptor.Scoped<IUserReferencePurger, FaultyPurger>());
        });
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var first = await InviteAsync(ownerClient, tenantId, NewEmail());
        var second = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, first.Id);
        await ActivateMembershipAsync(connectionString, second.Id);
        fault.UserId = first.UserId;

        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(ownerClient, tenantId, first.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(ownerClient, tenantId, second.Id)).StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () =>
            await CountUsersAsync(connection, second.UserId) == 0 &&
            await CountMembershipsAsync(connection, second.UserId) == 0);

        Assert.Equal(1, await CountUsersAsync(connection, first.UserId));
        Assert.Equal(1, await CountMembershipsAsync(connection, first.UserId));
        Assert.Equal(0, await CountInboxAsync(connection, first.Id));
    }

    private static string NewEmail() => $"member-{Guid.NewGuid():N}@example.com";

    private static string NewSlug() => $"org-{Guid.NewGuid():N}"[..12];

    // Registra un tenant (signup público habilitado) para tener un owner ya Active y un cliente
    // acotado a ese tenant por los headers del stub de desarrollo — misma receta que
    // MembershipLifecycleApiTests en Tenancy.
    private static async Task<(string TenantId, HttpClient Client)> RegisterTenantWithOwnerAsync(
        QepApiFactory factory)
    {
        using var bootstrapClient = CreateClient(
            factory, Guid.CreateVersion7().ToString(), Guid.CreateVersion7().ToString());
        bootstrapClient.DefaultRequestHeaders.Add("X-Email", NewEmail());
        bootstrapClient.DefaultRequestHeaders.Add("X-Email-Verified", "true");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register-tenant")
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
        var response = await bootstrapClient.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var registered = await response.Content.ReadFromJsonAsync<RegisterPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(registered);

        var client = CreateClient(
            factory, Guid.CreateVersion7().ToString(), registered!.TenantId.ToString());
        return (registered.TenantId.ToString(), client);
    }

    private static async Task<MembershipPayload> InviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        int? advisorCode = null)
    {
        var response = await SendInviteAsync(client, tenantId, email, advisorCode);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipPayload>(
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);
        return membership!;
    }

    private static async Task<HttpResponseMessage> SendInviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        int? advisorCode = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new
            {
                email,
                displayName = "Ana Pérez",
                roles = AdvisorRoles,
                advisorCode,
            }),
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<HttpResponseMessage> RemoveAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/remove");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // Saltea la vuelta invitar-aceptar (que exige un login real de Google), igual que en Tenancy.
    private static Task ActivateMembershipAsync(string connectionString, Guid membershipId) =>
        ExecuteAsync(
            connectionString,
            "UPDATE tenancy.memberships SET state = 'Active', accepted_at = now() WHERE id = @id",
            ("id", membershipId));

    private static Task SetMembershipStateAsync(
        string connectionString,
        Guid membershipId,
        string state) =>
        ExecuteAsync(
            connectionString,
            "UPDATE tenancy.memberships SET state = @state WHERE id = @id",
            ("state", state),
            ("id", membershipId));

    // identity.sessions no tiene FK a users (IdentityDbContext.ConfigureSession), así que el
    // worker tiene que borrarlas explícitamente: una fila viva acá es lo que lo prueba.
    private static Task SeedSessionAsync(string connectionString, Guid userId) =>
        ExecuteAsync(
            connectionString,
            """
            INSERT INTO identity.sessions
                (id, user_id, token_hash, created_at, last_seen_at, expires_at)
            VALUES (@id, @userId, @tokenHash, now(), now(), now() + interval '1 day')
            """,
            ("id", Guid.CreateVersion7()),
            ("userId", userId),
            ("tokenHash", Guid.NewGuid().ToString("N")));

    // Por el DbContext y no por la API: llegar a una cotización por HTTP exige cliente,
    // producto, escala y ciudad. Lo que importa acá es una sola columna: advisor_id.
    private static async Task SeedQuotationAsync(
        QepApiFactory factory,
        Guid tenantId,
        Guid advisorMembershipId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var advisor = new MemberId(advisorMembershipId);
        dbContext.Quotations.Add(Quotation.Create(
            QuotationId.New(),
            tenantId,
            "COT-2026-0001",
            Guid.CreateVersion7(),
            advisor,
            validUntil: null,
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            // El cliente de esta cotizacion de prueba no aplica retencion ni excedente de IVA:
            // lo que se ejercita aca es la limpieza de usuarios huerfanos, no los totales.
            customerWithRetention: false,
            customerVatSurplus: false,
            advisor,
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // Por el DbContext, misma razón que SeedQuotationAsync: lo que importa acá es approved_by o
    // cancelled_by, no el resto del contrato de pedidos. La cotización subyacente existe sólo
    // porque orders.quotation_id tiene FK — su contenido no se ejercita.
    private static async Task SeedOrderAsync(
        QepApiFactory factory,
        Guid tenantId,
        Guid convertedByMembershipId,
        Guid? approvedByMembershipId = null,
        Guid? cancelledByMembershipId = null)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotationsDbContext>();
        var convertedBy = new MemberId(convertedByMembershipId);
        var occurredAt = DateTimeOffset.UtcNow;
        var quotation = Quotation.Create(
            QuotationId.New(),
            tenantId,
            $"COT-{Guid.NewGuid():N}"[..20],
            Guid.CreateVersion7(),
            convertedBy,
            validUntil: null,
            paymentMethod: null,
            notes: null,
            QuotationParties.Empty,
            billingAccount: null,
            customerWithRetention: false,
            customerVatSurplus: false,
            convertedBy,
            occurredAt);
        dbContext.Quotations.Add(quotation);

        var order = Order.Create(
            OrderId.New(),
            tenantId,
            $"PED-{Guid.NewGuid():N}"[..20],
            quotation.Id,
            OrderPaymentStatus.PaymentPending,
            notes: null,
            convertedBy,
            proofs: [],
            occurredAt);

        if (approvedByMembershipId is { } approvedBy)
        {
            order.Approve(new MemberId(approvedBy), occurredAt);
        }

        if (cancelledByMembershipId is { } cancelledBy)
        {
            order.Cancel(new MemberId(cancelledBy), "Motivo de prueba", occurredAt);
        }

        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SeedFileAsync(
        QepApiFactory factory, Guid tenantId, Guid ownerUserId, FileOwnerType ownerType = FileOwnerType.User)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<StorageDbContext>();
        dbContext.FileResources.Add(FileResource.CreatePendingUpload(
            FileResourceId.New(),
            tenantId,
            ownerUserId,
            ownerType,
            "avatar.png",
            "image/png",
            1024,
            $"staging/{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<long> CountUsersAsync(NpgsqlConnection connection, Guid userId) =>
        ScalarAsync(connection, "SELECT COUNT(*) FROM identity.users WHERE id = @id", ("id", userId));

    // Sin FK desde tenancy.memberships a identity.users: lo único que borra estas filas es la
    // purga que el worker pide antes de borrar al usuario.
    private static Task<long> CountMembershipsAsync(NpgsqlConnection connection, Guid userId) =>
        ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM tenancy.memberships WHERE user_id = @id",
            ("id", userId));

    private static Task<long> CountSessionsAsync(NpgsqlConnection connection, Guid userId) =>
        ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM identity.sessions WHERE user_id = @id",
            ("id", userId));

    // El id del mensaje de outbox es el EventId del evento de dominio, que no se conoce desde
    // afuera; se llega por el payload, que lleva el membershipId.
    private static Task<long> CountInboxAsync(NpgsqlConnection connection, Guid membershipId) =>
        ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM identity.inbox_messages inbox
            JOIN platform.outbox_messages outbox ON outbox.id = inbox.message_id
            WHERE inbox.consumer = @consumer
              AND outbox.event_name = 'tenancy.membership-removed.v1'
              AND (outbox.payload -> 'membershipId' ->> 'value') = @membershipId
            """,
            ("consumer", Consumer),
            ("membershipId", membershipId.ToString()));

    private static Task DeleteInboxAsync(NpgsqlConnection connection, Guid membershipId) =>
        ExecuteAsync(
            connection,
            """
            DELETE FROM identity.inbox_messages inbox
            USING platform.outbox_messages outbox
            WHERE outbox.id = inbox.message_id
              AND inbox.consumer = @consumer
              AND (outbox.payload -> 'membershipId' ->> 'value') = @membershipId
            """,
            ("consumer", Consumer),
            ("membershipId", membershipId.ToString()));

    private static Task<long> CountAuditAsync(NpgsqlConnection connection, Guid resourceId, string action) =>
        ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM audit.entries WHERE resource_id = @id AND action = @action",
            ("id", resourceId.ToString()),
            ("action", action));

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(connection, sql, parameters);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (long)result!;
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        string sql,
        (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < PollTimeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Condition was not met within {PollTimeout.TotalSeconds:0} seconds.");
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

    private sealed record MembershipPayload(Guid Id, Guid UserId, string State, int? AdvisorCode);

    private sealed record ProblemPayload(string Code);

    /// <summary>A quién le falla la purga. Se fija después de invitar, antes de quitar.</summary>
    private sealed class PurgeFault
    {
        public Guid? UserId { get; set; }
    }

    // Deja rastreado, en el TenancyDbContext del scope, el borrado de la membresía del usuario de
    // PurgeFault y lanza sin guardar: es lo que dejaría un SaveChanges caído a mitad de purga.
    private sealed class FaultyPurger(PurgeFault fault, TenancyDbContext dbContext)
        : IUserReferencePurger
    {
        public string Source => "test-fault";

        public async Task PurgeAsync(Guid userId, CancellationToken cancellationToken)
        {
            if (userId != fault.UserId)
            {
                return;
            }

            var memberships = await dbContext.Memberships
                .Where(membership => membership.UserId == userId)
                .ToListAsync(cancellationToken);
            dbContext.Memberships.RemoveRange(memberships);
            throw new InvalidOperationException("Simulated purge failure.");
        }
    }

    private sealed class QepApiFactory(
        string connectionString,
        Action<IServiceCollection>? configureServices = null)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (configureServices is not null)
            {
                builder.ConfigureServices(configureServices);
            }

            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:QepDatabase", connectionString);
            builder.UseSetting("OpenTelemetry:Endpoint", string.Empty);
            builder.UseSetting("Storage:R2:AccountId", "test-account");
            builder.UseSetting("Storage:R2:AccessKeyId", "test-access-key");
            builder.UseSetting("Storage:R2:SecretAccessKey", "test-secret");
            builder.UseSetting("Storage:R2:Bucket", "test-bucket");
            // Fijado y no heredado (SDD-CT-17): con el proveedor de correo de appsettings.json y
            // sus credenciales ausentes, el validador de opciones tira la aplicación al arrancar.
            builder.UseSetting("Notifications:EmailProvider", "log");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
        }
    }
}
