using System.Net;
using System.Net.Http.Json;
using Modules.Identity.Application;
using Npgsql;
using static Modules.Identity.IntegrationTests.PasswordAuthApiHarness;

namespace Modules.Identity.IntegrationTests;

/// <summary>
/// Aceptar una invitación con contraseña contra el host real. Cada prueba levanta su propia base y
/// su propia fábrica, como <see cref="PasswordAuthApiTests"/>.
/// </summary>
public sealed class InvitationPasswordApiTests
{
    private const string OwnerPassword = "correct horse battery";

    [Fact]
    public async Task AnInvitedUserWithoutPasswordAcceptsByCreatingOne()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        var ct = TestContext.Current.CancellationToken;
        using var ownerClient = CreateClient(factory);
        using var anon = CreateClient(factory);
        using var other = CreateClient(factory);
        var owner = await RegisterAndVerifyAsync(ownerClient, cs, NewEmail(), OwnerPassword);
        var invited = await InviteAsync(ownerClient, cs, owner.TenantId, NewEmail());
        var token = invited.Token;
        var tenantId = owner.TenantId;
        var invitedUserId = invited.UserId;

        Assert.False((await GetInvitationAsync(anon, token)).HasPassword);
        var response = await AcceptWithPasswordAsync(anon, token, "a long enough password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Active", await MembershipStateAsync(cs, tenantId, invitedUserId));
        Assert.NotNull(await EmailVerifiedAtAsync(cs, invitedUserId));
        Assert.Contains(
            (await anon.GetFromJsonAsync<SessionPayload>("/api/v1/auth/me", ct))!.ActiveTenants,
            t => t.TenantId == tenantId);
        Assert.True((await GetInvitationAsync(other, token)).HasPassword);
    }

    [Fact]
    public async Task AnInvitedUserWithAPasswordAcceptsOnlyWithTheRightOne()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var ownerClient = CreateClient(factory);
        using var anon = CreateClient(factory);
        const string rightPassword = "the right long password";
        var owner = await RegisterAndVerifyAsync(ownerClient, cs, NewEmail(), OwnerPassword);
        var invitedEmail = NewEmail();
        var invited = await InviteAsync(ownerClient, cs, owner.TenantId, invitedEmail);

        // Sembrar la credencial antes de aceptar: olvidé mi contraseña -> reset.
        await ForgotAsync(anon, invitedEmail);
        var resetToken = await LatestOutboxTokenAsync(
            cs, IdentityIntegrationEvents.PasswordResetRequested, invited.UserId);
        Assert.Equal(HttpStatusCode.NoContent, (await ResetAsync(anon, resetToken, rightPassword)).StatusCode);
        var hashBefore = await CredentialHashAsync(cs, invited.UserId);

        var wrong = await AcceptWithPasswordAsync(anon, invited.Token, "the wrong long password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("identity.credentials.invalid", (await ProblemAsync(wrong)).Code);
        Assert.Equal(hashBefore, await CredentialHashAsync(cs, invited.UserId));
        Assert.Equal(
            HttpStatusCode.OK,
            (await AcceptWithPasswordAsync(anon, invited.Token, rightPassword)).StatusCode);
    }

    [Fact]
    public async Task AnExpiredInvitationIs422AndAnUnknownTokenIs404()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var ownerClient = CreateClient(factory);
        using var anon = CreateClient(factory);
        var owner = await RegisterAndVerifyAsync(ownerClient, cs, NewEmail(), OwnerPassword);
        var invited = await InviteAsync(ownerClient, cs, owner.TenantId, NewEmail());

        await using (var connection = new NpgsqlConnection(cs))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = new NpgsqlCommand(
                "UPDATE tenancy.memberships SET expires_at = now() - interval '1 minute' WHERE id = @id",
                connection);
            command.Parameters.AddWithValue("id", invited.MembershipId);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var expired = await AcceptWithPasswordAsync(anon, invited.Token, "a long enough password");
        var unknown = await AcceptWithPasswordAsync(anon, "no-such-token", "a long enough password");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, expired.StatusCode);
        Assert.Equal("tenancy.membership.invitation_expired", (await ProblemAsync(expired)).Code);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("tenancy.invitation.not_found", (await ProblemAsync(unknown)).Code);
    }

    [Fact]
    public async Task AShortPasswordOnAcceptFailsValidation()
    {
        await using var database = await StartDatabaseAsync();
        var cs = database.GetConnectionString();
        await using var factory = new PasswordAuthApiFactory(cs);
        using var ownerClient = CreateClient(factory);
        using var anon = CreateClient(factory);
        var owner = await RegisterAndVerifyAsync(ownerClient, cs, NewEmail(), OwnerPassword);
        var invited = await InviteAsync(ownerClient, cs, owner.TenantId, NewEmail());

        var response = await AcceptWithPasswordAsync(anon, invited.Token, "short");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ProblemAsync(response);
        Assert.Equal("validation.failed", problem.Code);
        Assert.True(problem.Errors!.ContainsKey("Password"));
        Assert.Equal("Invited", await MembershipStateAsync(cs, owner.TenantId, invited.UserId));
        Assert.Equal(0, await CountRowsAsync(cs, "identity.password_credentials", invited.UserId));
    }
}
