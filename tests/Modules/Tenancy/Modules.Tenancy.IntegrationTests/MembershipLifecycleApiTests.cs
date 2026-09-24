using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Modules.Tenancy.Application;
using Modules.Tenancy.Domain;
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

    /// <summary>
    /// Quitar a alguien no le cierra la puerta: el owner decidió que una persona quitada se
    /// puede volver a invitar. La fila se reutiliza —(user_id, tenant_id) es UNIQUE— y vuelve a
    /// Invited con roles, nombre y ventana nuevos, así que la persona tiene que aceptar otra vez.
    /// Hasta este cambio respondía 422 tenancy.membership.not_reinvitable.
    ///
    /// Sólo pasa por Reinvite si el usuario de Identity sobrevive a la baja: si
    /// OrphanUserCleanupWorker lo borra, la invitación crea un usuario y una membresía nuevos.
    /// La invitación viva en el otro tenant lo retiene (MembershipUserReferenceProbe), así que el
    /// resultado no depende de cuándo corra el worker. En producción lo retiene eso mismo, o una
    /// cotización que la persona ya hizo.
    /// </summary>
    [Fact]
    public async Task ReinvitingARemovedMemberRenewsTheSameMembership()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var (otherTenantId, _, _, otherOwnerClient) = await RegisterTenantWithOwnerAsync(factory);
        var email = NewEmail();
        var memberId = await InviteAsync(ownerClient, tenantId, email, AdvisorRoles);
        await InviteAsync(otherOwnerClient, otherTenantId, email, AdvisorRoles);
        await ActivateMembershipAsync(factory.ConnectionString, memberId);
        var removal = await SendActionAsync(ownerClient, tenantId, memberId, "remove");
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);
        var removed = await removal.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);

        var response = await SendInviteAsync(
            ownerClient, tenantId, email, AdminRoles, "Ana María Pérez");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var renewed = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(memberId, renewed!.Id);
        Assert.Equal("Invited", renewed.State);
        Assert.Null(renewed.AcceptedAt);
        Assert.True(
            renewed.ExpiresAt > DateTimeOffset.UtcNow,
            "A renewed invitation must expire in the future.");
        Assert.Equal(AdminRoles, renewed.Roles);
        Assert.Equal("Ana María Pérez", renewed.DisplayName);
        Assert.Equal(removed!.Version + 1, renewed.Version);

        // El email nuevo sale del evento re-emitido: sin él, la renovación no le llega a nadie.
        Assert.Equal(2L, await OutboxEventCountAsync(
            factory.ConnectionString, memberId, "tenancy.membership-invited.v1"));
        var outcomes = await AuditOutcomesAsync(
            factory.ConnectionString, memberId, "tenancy.membership.invited");
        Assert.Equal(["success", "success"], outcomes);

        // Y vuelve al roster, que esconde sólo las quitadas.
        var listResponse = await ownerClient.GetAsync(
            $"/api/v1/tenants/{tenantId}/memberships",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<MembershipListPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("Invited", Assert.Single(list!.Items, item => item.Id == memberId).State);
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

    // 200 con la fila entera y el ETag nuevo: el diálogo repinta la fila y ya tiene qué mandar
    // en el próximo If-Match. La auditoría va en la misma transacción que el cambio.
    [Fact]
    public async Task ProfileUpdateReturnsTheRowWithANewEtagAndIsAudited()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, "  Ana María Pérez  ", 12);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.Tag);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(memberId, membership!.Id);
        Assert.Equal("Ana María Pérez", membership.DisplayName);
        Assert.Equal(12, membership.AdvisorCode);
        Assert.Equal(2, membership.Version);
        Assert.Equal("Invited", membership.State);
        Assert.Equal(AdvisorRoles, membership.Roles);
        var outcomes = await AuditOutcomesAsync(
            factory.ConnectionString, memberId, "tenancy.membership.profile_updated");
        Assert.Equal("success", Assert.Single(outcomes));
    }

    // Guardar sin tocar nada no es un cambio: misma versión y nada auditado, o una pantalla
    // abierta en otro lado recibe un 412 falso (spec 2026-09-24, Dominio).
    [Fact]
    public async Task ProfileUpdateWithoutChangesKeepsTheVersionAndRecordsNothing()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(
            ownerClient, tenantId, NewEmail(), AdvisorRoles, advisorCode: 12);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, DefaultDisplayName, 12);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.Tag);
        Assert.Empty(await AuditOutcomesAsync(
            factory.ConnectionString, memberId, "tenancy.membership.profile_updated"));
    }

    [Fact]
    public async Task ProfileUpdateRequiresIfMatch()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, "Ana María Pérez", 12, expectedVersion: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("precondition.if_match_required", problem!.Code);
    }

    [Fact]
    public async Task ProfileUpdateWithAStaleVersionIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, "Ana María Pérez", 12, expectedVersion: 99);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task ProfileUpdateWithAZeroCodeMarksTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, DefaultDisplayName, 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("AdvisorCode", out _));
    }

    // Review Focus 3: un decimal no puede salir como 500.
    [Fact]
    public async Task ProfileUpdateWithADecimalCodeMarksTheField()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, DefaultDisplayName, 12.5m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("validation.failed", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("AdvisorCode", out _));
    }

    // D3: el código ya lo tiene otra membresía del tenant.
    [Fact]
    public async Task ProfileUpdateWithACodeTakenInTheTenantIsRejected()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles, advisorCode: 7);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            ownerClient, tenantId, memberId, DefaultDisplayName, 7);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal("tenancy.membership.advisor_code_taken", problem!.Code);
    }

    // D3: cada tenant tiene su propio sistema externo, así que el mismo código vale en otro.
    [Fact]
    public async Task TheSameCodeInAnotherTenantIsAccepted()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var (otherTenantId, _, _, otherOwnerClient) = await RegisterTenantWithOwnerAsync(factory);
        await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles, advisorCode: 7);
        var otherMemberId = await InviteAsync(
            otherOwnerClient, otherTenantId, NewEmail(), AdvisorRoles);

        var response = await SendProfileAsync(
            otherOwnerClient, otherTenantId, otherMemberId, DefaultDisplayName, 7);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Equal(7, membership!.AdvisorCode);
    }

    // D1: mandar null borra el código, y borrarlo lo libera para otra persona.
    [Fact]
    public async Task ClearingTheCodeWithNullFreesItForAnotherMember()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var holder = await InviteAsync(
            ownerClient, tenantId, NewEmail(), AdvisorRoles, advisorCode: 7);
        var other = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var cleared = await SendProfileAsync(
            ownerClient, tenantId, holder, DefaultDisplayName, null);

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var clearedRow = await cleared.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        Assert.Null(clearedRow!.AdvisorCode);
        Assert.Equal(2, clearedRow.Version);

        var taken = await SendProfileAsync(ownerClient, tenantId, other, DefaultDisplayName, 7);

        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
    }

    // 403 y nunca 404: la ruta pide un tenant que no es el del contexto (doble capa).
    [Fact]
    public async Task ProfileUpdateFromAnotherTenantIsForbidden()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, ownerMembershipId, _, _) = await RegisterTenantWithOwnerAsync(factory);
        using var otherClient = CreateClient(factory, OtherSubjectId, OtherTenantId);

        var response = await SendProfileAsync(
            otherClient, tenantId, ownerMembershipId, "Ana María Pérez", 12);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ProfileUpdateRequiresTheManagePermission()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        using var readerOnly = CreateClient(factory, Guid.CreateVersion7().ToString(), tenantId);
        readerOnly.DefaultRequestHeaders.Add("X-Permissions", "advisorship.read");

        var response = await SendProfileAsync(
            readerOnly, tenantId, memberId, "Ana María Pérez", 12);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // D7 paso 3: display-name se retiró; el frontend ya usa PUT .../profile. La ruta ya no
    // existe, así que responde 404 y no 405 — no hay ningún verbo mapeado en ese path.
    [Fact]
    public async Task TheRetiredDisplayNameEndpointIsGone()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var memberId = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);

        var response = await SendDisplayNameAsync(
            ownerClient, tenantId, memberId, "Ana María Pérez");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // D3: único por tenant sólo cuando existe, y sin filtrar por estado (D4). El índice es la
    // autoridad ante una carrera, así que se verifica su forma en la base y no sólo su efecto.
    [Fact]
    public async Task TheAdvisorCodeIndexIsUniquePerTenantAndSkipsMembersWithoutCode()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        // Registrar arranca la API, y arrancarla aplica las migraciones.
        await RegisterTenantWithOwnerAsync(factory);

        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT indexdef FROM pg_indexes
            WHERE schemaname = 'tenancy' AND indexname = 'IX_memberships_tenant_id_advisor_code'
            """,
            connection);
        var definition = (string?)await command.ExecuteScalarAsync(
            TestContext.Current.CancellationToken);

        Assert.NotNull(definition);
        Assert.Contains("CREATE UNIQUE INDEX", definition, StringComparison.Ordinal);
        Assert.Contains("(tenant_id, advisor_code)", definition, StringComparison.Ordinal);
        Assert.Contains("WHERE (advisor_code IS NOT NULL)", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("state", definition, StringComparison.Ordinal);
    }

    // Review Focus 1: dos requests que pasan el chequeo previo a la vez. El segundo llega a la
    // base, y el 23505 de este índice tiene que salir como el código de dominio (422) y no como
    // un 500. Se ejerce saltándose el handler, que es exactamente lo que pasa en la carrera.
    // Las dos membresías nacen sin código y conviven: el índice es parcial.
    [Fact]
    public async Task ADuplicateCodeThatReachesTheDatabaseIsTheDomainCodeAndNotAServerError()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var first = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        var second = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        await SetAdvisorCodeThroughTheAggregateAsync(factory, tenantId, first, 7);

        var error = await Assert.ThrowsAsync<TenantDomainException>(
            () => SetAdvisorCodeThroughTheAggregateAsync(factory, tenantId, second, 7));

        Assert.Equal("tenancy.membership.advisor_code_taken", error.Code);
    }

    // D4 y el chequeo previo: una quitada sigue ocupando su código, la propia membresía no se
    // cuenta a sí misma, y otro tenant tiene su propio espacio de códigos (D3).
    [Fact]
    public async Task IsAdvisorCodeTakenSeesRemovedMembersSkipsTheExcludedOneAndIgnoresOtherTenants()
    {
        await using var database = await StartDatabaseAsync();
        using var factory = new QepApiFactory(database.GetConnectionString());
        var (tenantId, _, _, ownerClient) = await RegisterTenantWithOwnerAsync(factory);
        var (otherTenantId, _, _, _) = await RegisterTenantWithOwnerAsync(factory);
        var holder = await InviteAsync(ownerClient, tenantId, NewEmail(), AdvisorRoles);
        await SetAdvisorCodeThroughTheAggregateAsync(factory, tenantId, holder, 7);
        var removal = await SendActionAsync(ownerClient, tenantId, holder, "remove");
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var memberships = scope.ServiceProvider.GetRequiredService<IMembershipRepository>();
        var tenant = new TenantId(Guid.Parse(tenantId));
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.True(await memberships.IsAdvisorCodeTakenAsync(tenant, 7, null, cancellationToken));
        Assert.False(await memberships.IsAdvisorCodeTakenAsync(
            tenant, 7, new MembershipId(holder), cancellationToken));
        Assert.False(await memberships.IsAdvisorCodeTakenAsync(
            new TenantId(Guid.Parse(otherTenantId)), 7, null, cancellationToken));
        Assert.False(await memberships.IsAdvisorCodeTakenAsync(tenant, 8, null, cancellationToken));
    }

    private static readonly string[] AdvisorRoles = ["advisor"];
    private static readonly string[] AdminRoles = ["admin"];
    private const string DefaultDisplayName = "Ana Pérez";

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
        IReadOnlyCollection<string>? roles = null,
        string displayName = DefaultDisplayName,
        int? advisorCode = null)
    {
        var response = await SendInviteAsync(
            client, tenantId, email, roles ?? AdvisorRoles, displayName, advisorCode);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var membership = await response.Content.ReadFromJsonAsync<MembershipListItemPayload>(
            TestContext.Current.CancellationToken);
        return membership!.Id;
    }

    private static async Task<HttpResponseMessage> SendInviteAsync(
        HttpClient client,
        string tenantId,
        string email,
        IReadOnlyCollection<string> roles,
        string displayName,
        int? advisorCode = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/tenants/{tenantId}/memberships")
        {
            Content = JsonContent.Create(new { email, displayName, roles, advisorCode })
        };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
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
            HttpMethod.Put,
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

    private static async Task<HttpResponseMessage> SendDisplayNameAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId,
        string? displayName,
        long? expectedVersion = 1)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/display-name")
        {
            Content = JsonContent.Create(new { displayName })
        };
        if (expectedVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // advisorCode es object para poder mandar un decimal (Review Focus 3).
    private static async Task<HttpResponseMessage> SendProfileAsync(
        HttpClient client,
        string tenantId,
        Guid membershipId,
        string? displayName,
        object? advisorCode,
        long? expectedVersion = 1)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/profile")
        {
            Content = JsonContent.Create(new { displayName, advisorCode })
        };
        if (expectedVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{expectedVersion}\"");
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // Carga la membresía y le pone el código por el agregado, sin pasar por ningún handler: es
    // lo que deja a la prueba llegar al índice sin el chequeo previo.
    private static async Task SetAdvisorCodeThroughTheAggregateAsync(
        QepApiFactory factory,
        string tenantId,
        Guid membershipId,
        int? advisorCode)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var memberships = scope.ServiceProvider.GetRequiredService<IMembershipRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<ITenancyUnitOfWork>();
        var membership = await memberships.FindByIdAsync(
            new MembershipId(membershipId),
            new TenantId(Guid.Parse(tenantId)),
            TestContext.Current.CancellationToken);
        Assert.NotNull(membership);

        membership.UpdateProfile(
            membership.DisplayName ?? DefaultDisplayName, advisorCode, DateTimeOffset.UtcNow);
        await unitOfWork.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<string>> AuditOutcomesAsync(
        string connectionString,
        Guid membershipId,
        string action)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT outcome FROM audit.entries
            WHERE resource_id = @resourceId AND action = @action
            """,
            connection);
        command.Parameters.AddWithValue("resourceId", membershipId.ToString());
        command.Parameters.AddWithValue("action", action);
        var outcomes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            outcomes.Add(reader.GetString(0));
        }

        return outcomes;
    }

    private static async Task<long> OutboxEventCountAsync(
        string connectionString,
        Guid membershipId,
        string eventName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM platform.outbox_messages
            WHERE event_name = @eventName
              AND payload::text LIKE '%' || @membershipId || '%'
            """,
            connection);
        command.Parameters.AddWithValue("eventName", eventName);
        command.Parameters.AddWithValue("membershipId", membershipId.ToString());
        var count = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (long)count!;
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
        bool IsOwner,
        string? DisplayName,
        int? AdvisorCode);

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
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:DryRun", "true");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:MinimumAgeHours", "24");
            builder.UseSetting("Storage:PaymentProofOrphanCleanup:IntervalHours", "24");
            builder.UseSetting("Quotations:PaymentProofs:PublicLinks", "false");
            builder.UseSetting("Registration:PublicTenantSignupEnabled", "true");
        }
    }
}
