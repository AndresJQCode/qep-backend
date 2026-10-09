using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Modules.Catalog.Domain;
using Modules.Catalog.Infrastructure.Persistence;
using Modules.Identity.Infrastructure.Messaging;
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
/// sus membresías (Quotations), un archivo del que es dueño (Storage) o un cambio de precio que
/// hizo (Catalog). Auditoría y notificaciones no retienen: son append-only y guardan snapshot.
///
/// Cuando nada lo retiene, antes de borrarlo se purgan sus membresías quitadas o vencidas
/// (<c>IUserReferencePurger</c>, spec 2026-10-02), lo que libera su código de asesor.
/// </summary>
public sealed class OrphanUserCleanupTests
{
    private const string Consumer = OrphanUserCleanupWorker.Consumer;
    private const string RemovedEvent = OrphanUserCleanupWorker.RemovedEvent;
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

    // Spec 2026-10-05: invoiced_by también retiene. El facturador es una tercera membresía y la
    // aprobación la da el asesor, así que lo único que apunta al facturador es invoiced_by.
    [Fact]
    public async Task InvoicingAnOrderKeepsTheUser()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var advisor = await InviteAsync(ownerClient, tenantId, NewEmail());
        var biller = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, advisor.Id);
        await ActivateMembershipAsync(connectionString, biller.Id);
        await SeedOrderAsync(
            factory,
            Guid.Parse(tenantId),
            convertedByMembershipId: advisor.Id,
            approvedByMembershipId: advisor.Id,
            invoicedByMembershipId: biller.Id);

        var removal = await RemoveAsync(ownerClient, tenantId, biller.Id);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await CountInboxAsync(connection, biller.Id) == 1);

        Assert.Equal(1, await CountUsersAsync(connection, biller.UserId));
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
    /// El histórico de precios es permanente y el reporte de cambios muestra el correo de quien
    /// cambió el precio (<c>product_price_changes.changed_by</c>, un id de usuario y no de
    /// membresía). Quien lo cambió tiene historia: ni su usuario ni su membresía quitada se
    /// borran, y su código de asesor sigue ocupado (D4).
    /// </summary>
    [Fact]
    public async Task AuthoringAProductPriceChangeKeepsTheUserAndItsRemovedMembership()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        using var factory = new QepApiFactory(connectionString);
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var member = await InviteAsync(ownerClient, tenantId, NewEmail(), advisorCode: 7);
        await ActivateMembershipAsync(connectionString, member.Id);
        await SeedPriceChangeAsync(factory, Guid.Parse(tenantId), changedByUserId: member.UserId);

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
    /// borró.
    ///
    /// Las dos bajas entran a la base en un solo commit, A primero, para que el worker las vea en
    /// el mismo lote. Por la API no se puede garantizar: si el worker toma a A en un tick en que B
    /// todavía no existe, A queda esperando su reintento y B se procesa sola, en un lote donde no
    /// hay nada que filtrar, y la prueba pasaría sin probar nada.
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

        await RemoveInOneCommitAsync(connectionString, first, second);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () =>
            await CountUsersAsync(connection, second.UserId) == 0 &&
            await CountMembershipsAsync(connection, second.UserId) == 0);

        Assert.Equal(1, await CountUsersAsync(connection, first.UserId));
        Assert.Equal(1, await CountMembershipsAsync(connection, first.UserId));
        Assert.Equal(0, await CountInboxAsync(connection, first.Id));
        // A sí se intentó, y falló: está reclamado, esperando su reintento.
        Assert.Equal(1, await InboxAttemptsAsync(connection, first.Id));
    }

    /// <summary>
    /// Un mensaje que falla siempre no se reintenta en cada tick: queda reclamado hasta su próximo
    /// intento, con esperas que crecen (<see cref="ClaimedOutboxConsumer.LeaseFor"/>) hasta la
    /// última y se quedan ahí. Nunca se abandona: cuando la falla se arregla, el reintento siguiente
    /// borra al usuario. El reloj es de la prueba: avanzarlo es lo que vence cada espera, así que la
    /// prueba no duerme minutos.
    /// </summary>
    [Fact]
    public async Task AFailingMessageKeepsBeingRetriedWithACappedBackoff()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var fault = new PurgeFault();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var factory = new QepApiFactory(connectionString, services =>
        {
            services.AddSingleton(fault);
            services.Insert(0, ServiceDescriptor.Scoped<IUserReferencePurger, FaultyPurger>());
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(clock);
        });
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var poisoned = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, poisoned.Id);
        fault.UserId = poisoned.UserId;
        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(ownerClient, tenantId, poisoned.Id)).StatusCode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () => await InboxAttemptsAsync(connection, poisoned.Id) == 1);

        // Una baja sana que se procesa después del primer fallo prueba que corrió al menos un tick
        // más, y en ese tick el mensaje fallido no se volvió a intentar: su espera no venció.
        await RemoveAMemberAndWaitForItsCleanupAsync(connectionString, connection, ownerClient, tenantId);
        Assert.Equal(1, await InboxAttemptsAsync(connection, poisoned.Id));

        // Más allá del cuarto intento, que es donde antes se abandonaba: sigue sin terminar.
        var claims = Worker(factory).Claims;
        const int lastFailedAttempt = 6;
        for (var attempt = 2; attempt <= lastFailedAttempt; attempt++)
        {
            var expected = attempt;
            clock.Advance(claims.LeaseFor(attempt - 1) + TimeSpan.FromSeconds(1));
            await WaitUntilAsync(async () => await InboxAttemptsAsync(connection, poisoned.Id) == expected);
            Assert.Equal(0, await CountInboxAsync(connection, poisoned.Id));
        }

        // La espera se satura en la última de la curva: el reclamo vigente dura 15 minutos desde
        // que se tomó, que es el instante congelado del reloj.
        var longestWait = claims.Leases[^1];
        Assert.Equal(clock.UtcNow + longestWait, await ClaimedUntilAsync(connection, poisoned.Id));

        // Antes de que venza, otro tick no lo retoma.
        clock.Advance(longestWait - TimeSpan.FromMinutes(1));
        await RemoveAMemberAndWaitForItsCleanupAsync(connectionString, connection, ownerClient, tenantId);
        Assert.Equal(lastFailedAttempt, await InboxAttemptsAsync(connection, poisoned.Id));

        // Se arregla la falla: el reintento siguiente borra al usuario y su membresía, y termina el
        // mensaje. Abandonarlo habría dejado ese residuo para siempre.
        fault.UserId = null;
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await WaitUntilAsync(async () =>
            await CountUsersAsync(connection, poisoned.UserId) == 0 &&
            await CountMembershipsAsync(connection, poisoned.UserId) == 0);

        Assert.Equal(1, await CountInboxAsync(connection, poisoned.Id));
        Assert.Equal(lastFailedAttempt + 1, await InboxAttemptsAsync(connection, poisoned.Id));
        Assert.Equal(1, await CountAuditAsync(connection, poisoned.UserId, "identity.user.deleted"));
    }

    /// <summary>
    /// Mensajes que fallan siempre no pueden ocupar el lote para siempre. Se siembran tantos como
    /// entran en un lote, todos más viejos que una baja sana: sin backoff, cada tick tomaba esos
    /// mismos, en orden de llegada, fallaban, y la baja sana no llegaba nunca al lote. Con backoff,
    /// cada uno queda reclamado hasta su reintento y el tick siguiente llena el lote con lo que sí
    /// toca.
    /// </summary>
    [Fact]
    public async Task MessagesThatKeepFailingDoNotStarveANewerOne()
    {
        await using var database = await StartDatabaseAsync();
        var connectionString = database.GetConnectionString();
        var fault = new PurgeFault();
        using var factory = new QepApiFactory(connectionString, services =>
        {
            services.AddSingleton(fault);
            services.Insert(0, ServiceDescriptor.Scoped<IUserReferencePurger, FaultyPurger>());
        });
        var (tenantId, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var poisoned = await InviteAsync(ownerClient, tenantId, NewEmail());
        // Quitada y no activa: con una membresía viva la sonda de Tenancy retendría al usuario y el
        // mensaje terminaría bien, sin llegar al purgador que falla.
        await SetMembershipStateAsync(connectionString, poisoned.Id, "Removed");
        fault.UserId = poisoned.UserId;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // En un solo commit: el primer tick que los ve, los ve a todos.
        await using (var transaction = await connection.BeginTransactionAsync(
            TestContext.Current.CancellationToken))
        {
            var occurredAt = DateTimeOffset.UtcNow.AddHours(-1);
            for (var index = 0; index < OrphanUserCleanupWorker.BatchSize; index++)
            {
                await InsertRemovedEventAsync(
                    connection, poisoned.UserId, poisoned.Id, occurredAt.AddSeconds(index));
            }

            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        var healthy = await RemoveAMemberAndWaitForItsCleanupAsync(
            connectionString, connection, ownerClient, tenantId);

        Assert.Equal(1, await CountInboxAsync(connection, healthy.Id));
        Assert.Equal(1, await CountUsersAsync(connection, poisoned.UserId));
        Assert.Equal(0, await CountInboxAsync(connection, poisoned.Id));
        Assert.Equal(OrphanUserCleanupWorker.BatchSize, await CountPendingClaimsAsync(connection, poisoned.Id));
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
            defaultCurrency: QuotationCurrency.Cop,
            // El cliente de esta cotizacion de prueba no aplica retencion ni excedente de IVA:
            // lo que se ejercita aca es la limpieza de usuarios huerfanos, no los totales.
            customerWithRetention: false,
            customerVatSurplus: false,
            advisor,
            DateTimeOffset.UtcNow));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // Por el DbContext, misma razón que SeedQuotationAsync: lo que importa acá es approved_by,
    // cancelled_by o invoiced_by, no el resto del contrato de pedidos. La cotización subyacente
    // existe sólo porque orders.quotation_id tiene FK — su contenido no se ejercita.
    private static async Task SeedOrderAsync(
        QepApiFactory factory,
        Guid tenantId,
        Guid convertedByMembershipId,
        Guid? approvedByMembershipId = null,
        Guid? cancelledByMembershipId = null,
        Guid? invoicedByMembershipId = null)
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
            defaultCurrency: QuotationCurrency.Cop,
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

        // Facturar exige Approved (spec 2026-10-05, decisión 1): quien pida invoicedBy tiene que
        // pedir también approvedBy.
        if (invoicedByMembershipId is { } invoicedBy)
        {
            order.Invoice(new MemberId(invoicedBy), occurredAt);
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

    // Por el DbContext, misma razón que SeedQuotationAsync: llegar por HTTP exige el permiso de
    // catálogo en el stub y un PUT completo. Lo que importa acá es changed_by. El producto existe
    // sólo porque product_price_changes.product_id tiene FK, y la fila la arma
    // ProductPriceChangeDetector, que es el único que puede crearla (sus fábricas son internal).
    private static async Task SeedPriceChangeAsync(
        QepApiFactory factory,
        Guid tenantId,
        Guid changedByUserId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var occurredAt = DateTimeOffset.UtcNow;
        var product = Product.Create(
            ProductId.New(),
            tenantId,
            "Vela de soja",
            $"VS-{Guid.NewGuid():N}"[..12],
            ProductDetails.Empty,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 100m } },
            occurredAt);
        dbContext.Products.Add(product);
        dbContext.ProductPriceChanges.AddRange(ProductPriceChangeDetector.Detect(
            product,
            new ProductPricing { Prices = new Dictionary<string, decimal> { ["USD"] = 120m } },
            changedByUserId,
            occurredAt));
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
    // afuera; se llega por el payload, que lleva el membershipId. Cuenta sólo los terminados: un
    // mensaje reclamado y sin terminar ya tiene su fila, con processed_at en null.
    private static Task<long> CountInboxAsync(NpgsqlConnection connection, Guid membershipId) =>
        ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM identity.inbox_messages inbox
            JOIN platform.outbox_messages outbox ON outbox.id = inbox.message_id
            WHERE inbox.consumer = @consumer
              AND inbox.processed_at IS NOT NULL
              AND outbox.event_name = @eventName
              AND (outbox.payload -> 'membershipId' ->> 'value') = @membershipId
            """,
            ("consumer", Consumer),
            ("eventName", RemovedEvent),
            ("membershipId", membershipId.ToString()));

    // Los intentos del mensaje de esa baja, terminado o no; 0 si nunca se reclamó.
    private static Task<long> InboxAttemptsAsync(NpgsqlConnection connection, Guid membershipId) =>
        ScalarAsync(
            connection,
            """
            SELECT COALESCE(SUM(inbox.attempts), 0)::bigint FROM identity.inbox_messages inbox
            JOIN platform.outbox_messages outbox ON outbox.id = inbox.message_id
            WHERE inbox.consumer = @consumer
              AND outbox.event_name = @eventName
              AND (outbox.payload -> 'membershipId' ->> 'value') = @membershipId
            """,
            ("consumer", Consumer),
            ("eventName", RemovedEvent),
            ("membershipId", membershipId.ToString()));

    // Hasta cuándo está reclamado el mensaje de esa baja, que es cuándo le toca el reintento.
    private static async Task<DateTimeOffset> ClaimedUntilAsync(NpgsqlConnection connection, Guid membershipId)
    {
        await using var command = CreateCommand(
            connection,
            """
            SELECT inbox.claimed_until FROM identity.inbox_messages inbox
            JOIN platform.outbox_messages outbox ON outbox.id = inbox.message_id
            WHERE inbox.consumer = @consumer
              AND outbox.event_name = @eventName
              AND (outbox.payload -> 'membershipId' ->> 'value') = @membershipId
            """,
            [("consumer", Consumer), ("eventName", RemovedEvent), ("membershipId", membershipId.ToString())]);
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return new DateTimeOffset((DateTime)result!, TimeSpan.Zero);
    }

    // Mensajes de esa baja reclamados una vez y sin terminar: fallaron y esperan su reintento.
    private static Task<long> CountPendingClaimsAsync(NpgsqlConnection connection, Guid membershipId) =>
        ScalarAsync(
            connection,
            """
            SELECT COUNT(*) FROM identity.inbox_messages inbox
            JOIN platform.outbox_messages outbox ON outbox.id = inbox.message_id
            WHERE inbox.consumer = @consumer
              AND inbox.processed_at IS NULL
              AND inbox.attempts = 1
              AND outbox.event_name = @eventName
              AND (outbox.payload -> 'membershipId' ->> 'value') = @membershipId
            """,
            ("consumer", Consumer),
            ("eventName", RemovedEvent),
            ("membershipId", membershipId.ToString()));

    // Un tenancy.membership-removed.v1 escrito a mano, con lo único que el worker y estas pruebas
    // leen del payload. Va con processed_at para que el publicador de Tenancy no lo despache: lo
    // que se prueba es el consumidor de Identity, que lee el outbox sin mirar esa columna.
    private static Task InsertRemovedEventAsync(
        NpgsqlConnection connection,
        Guid userId,
        Guid membershipId,
        DateTimeOffset occurredAt) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO platform.outbox_messages
                (id, event_name, payload, correlation_id, occurred_at, processed_at, attempts)
            VALUES (@id, @eventName,
                    jsonb_build_object(
                        'membershipId', jsonb_build_object('value', @membershipId::text),
                        'userId', @userId::text),
                    'orphan-user-cleanup-tests', @occurredAt, now(), 0)
            """,
            ("id", Guid.CreateVersion7()),
            ("eventName", RemovedEvent),
            ("membershipId", membershipId),
            ("userId", userId),
            ("occurredAt", occurredAt));

    // Quita las membresías y escribe sus eventos en un solo commit, en el orden recibido: el
    // worker las ve todas en el mismo lote, o ninguna.
    private static async Task RemoveInOneCommitAsync(
        string connectionString,
        params MembershipPayload[] members)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var member in members)
        {
            await ExecuteAsync(
                connection,
                "UPDATE tenancy.memberships SET state = 'Removed' WHERE id = @id",
                ("id", member.Id));
            await InsertRemovedEventAsync(connection, member.UserId, member.Id, occurredAt);
            occurredAt = occurredAt.AddSeconds(1);
        }

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
    }

    // La instancia que corre en la aplicación de la prueba: su curva es la que se ejerce.
    private static OrphanUserCleanupWorker Worker(QepApiFactory factory) =>
        factory.Services.GetServices<IHostedService>().OfType<OrphanUserCleanupWorker>().Single();

    // Una baja sana, de punta a punta, para saber que el worker corrió otro tick.
    private static async Task<MembershipPayload> RemoveAMemberAndWaitForItsCleanupAsync(
        string connectionString,
        NpgsqlConnection connection,
        HttpClient ownerClient,
        string tenantId)
    {
        var member = await InviteAsync(ownerClient, tenantId, NewEmail());
        await ActivateMembershipAsync(connectionString, member.Id);
        Assert.Equal(HttpStatusCode.OK, (await RemoveAsync(ownerClient, tenantId, member.Id)).StatusCode);
        await WaitUntilAsync(async () => await CountUsersAsync(connection, member.UserId) == 0);
        return member;
    }

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

        public async Task<int> PurgeAsync(Guid userId, CancellationToken cancellationToken)
        {
            if (userId != fault.UserId)
            {
                return 0;
            }

            var memberships = await dbContext.Memberships
                .Where(membership => membership.UserId == userId)
                .ToListAsync(cancellationToken);
            dbContext.Memberships.RemoveRange(memberships);
            throw new InvalidOperationException("Simulated purge failure.");
        }
    }

    // Reloj que sólo avanza cuando la prueba lo pide: así se vence la espera de un reintento sin
    // dormir minutos. Arranca truncado a microsegundos, igual que SystemClock, para que lo que se
    // guarda en timestamptz vuelva igual.
    private sealed class MutableClock(DateTimeOffset start) : IClock
    {
        private long _ticks = start.UtcTicks - (start.UtcTicks % TimeSpan.TicksPerMicrosecond);

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
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
