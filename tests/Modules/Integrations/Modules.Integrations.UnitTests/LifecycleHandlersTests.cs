using BuildingBlocks.Application;
using FluentValidation;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Estados y transiciones» y los endpoints <c>test</c>, <c>pause</c>, <c>resume</c> y
/// <c>DELETE</c>: If-Match donde el spec lo pide, eventos de outbox y auditoría en el mismo guardado.
/// </summary>
public sealed class LifecycleHandlersTests
{
    private static readonly DateTimeOffset Later = IntegrationsTestBed.Now.AddHours(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // P10: siempre 200 con la conexión; si pasa, anota la verificación y saca de NeedsAttention.
    [Fact]
    public async Task APassingTestRecordsTheVerificationAndBringsNeedsAttentionBack()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);
        bed.Clock.UtcNow = Later;

        var response = await bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct);

        Assert.Equal("Active", response.Status);
        Assert.Equal(Later, response.LastVerifiedAt);
        Assert.Equal(IntegrationsTestBed.Token, Assert.Single(bed.Tester.Calls).Secrets[ZenviaFieldKeys.ApiToken]);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal("integrations.connection.verified", audit.Action);
        Assert.Equal("success", audit.Outcome);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    // P10 y DECISIÓN 2 (owner 2026-10-08): un rechazo sobre una Active la pasa a NeedsAttention, con
    // evento y auditoría, y responde 200 con la conexión.
    [Fact]
    public async Task ARejectedTestOnAnActiveConnectionMovesItToNeedsAttention()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;
        bed.Clock.UtcNow = Later;

        var response = await bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct);

        Assert.Equal("NeedsAttention", response.Status);
        Assert.Equal("credentials_rejected", response.LastFailureCode);
        Assert.Equal(Later, response.LastFailureAt);
        Assert.Equal(
            ["integrations.connection.verified", "integrations.connection.needs_attention"],
            bed.Audit.Entries.Select(entry => entry.Action));
        Assert.Equal("failure", bed.Audit.Entries[0].Outcome);
        Assert.Equal("integrations.connection-needs-attention.v1", Assert.Single(bed.Events.Published).EventName);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    // Unreachable no es un rechazo: anota la falla y deja el estado.
    [Fact]
    public async Task AnUnreachableTestRecordsTheFailureAndKeepsTheStatus()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Tester.Result = ConnectionTestResult.Unreachable("timeout");
        bed.Clock.UtcNow = Later;

        var response = await bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct);

        Assert.Equal("Active", response.Status);
        Assert.Equal("provider_unreachable", response.LastFailureCode);
        Assert.Equal(Later, response.LastFailureAt);
        Assert.Equal(2, response.Version);
        Assert.Equal("failure", Assert.Single(bed.Audit.Entries).Outcome);
        Assert.Empty(bed.Events.Published);
    }

    // Sólo una Active transiciona: una Paused sigue Paused (resume vuelve a probar, D4) y una que ya
    // estaba en NeedsAttention no repite el evento.
    [Theory]
    [InlineData("Paused")]
    [InlineData("NeedsAttention")]
    public async Task ARejectedTestOutsideActiveOnlyRecordsTheFailure(string status)
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        if (status == "Paused")
        {
            connection.Pause(IntegrationsTestBed.Now);
        }
        else
        {
            connection.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);
        }

        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;
        bed.Clock.UtcNow = Later;

        var response = await bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct);

        Assert.Equal(status, response.Status);
        Assert.Equal(Later, response.LastFailureAt);
        Assert.Equal("failure", Assert.Single(bed.Audit.Entries).Outcome);
        Assert.Empty(bed.Events.Published);
    }

    [Fact]
    public async Task ATestWithAnUnreadableSecretIs422AndSavesNothing()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Protector.Unreadable.Add((connection.Id, ZenviaFieldKeys.ApiToken));

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct));

        Assert.Equal("secrets.apiToken", Assert.Single(error.Errors).PropertyName);
        Assert.Empty(bed.Tester.Calls);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task PauseMovesActiveToPausedAuditsAndPublishes()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var response = await bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct);

        Assert.Equal("Paused", response.Status);
        Assert.Equal(2, response.Version);
        Assert.Equal("integrations.connection.paused", Assert.Single(bed.Audit.Entries).Action);
        var published = Assert.Single(bed.Events.Published);
        Assert.Equal("integrations.connection-paused.v1", published.EventName);
        Assert.Equal(connection.Id, published.ConnectionId);
        Assert.Equal("zenvia", published.ProviderKey);
    }

    [Fact]
    public async Task PausingTwiceIsNotActiveAndAStaleVersionIs412()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        await bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct);

        Assert.Equal("integrations.connection.not_active", (await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 2), Ct))).Code);
        Assert.Equal("concurrency.conflict", (await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct))).Code);
    }

    // D4: reanudar vuelve a probar.
    [Fact]
    public async Task ResumeRetestsAndActivates()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.Pause(IntegrationsTestBed.Now);

        var response = await bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 2), Ct);

        Assert.Equal("Active", response.Status);
        Assert.Single(bed.Tester.Calls);
        Assert.Equal("integrations.connection.resumed", Assert.Single(bed.Audit.Entries).Action);
    }

    // Spec, «Paused → Active»: si la credencial ya no sirve, queda NeedsAttention (guardado) y el resume
    // responde 422 credentials_rejected.
    [Fact]
    public async Task ResumeWithRejectedCredentialsLeavesNeedsAttentionSavedAndAnswers422()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.Pause(IntegrationsTestBed.Now);
        bed.Tester.Result = ConnectionTestResult.CredentialsRejected;

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 2), Ct));

        Assert.Equal("integrations.connection.credentials_rejected", error.Code);
        Assert.Equal(["secrets.apiToken"], error.FieldErrors.Keys);
        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Equal("credentials_rejected", connection.LastFailureCode);
        Assert.Equal(1, bed.UnitOfWork.Saves);
        Assert.Equal("integrations.connection.needs_attention", Assert.Single(bed.Audit.Entries).Action);
        Assert.Equal("integrations.connection-needs-attention.v1", Assert.Single(bed.Events.Published).EventName);
    }

    // P11: «no pude verificar» deja la conexión pausada y no guarda nada.
    [Fact]
    public async Task ResumeWithAnUnreachableProviderStaysPausedAndSavesNothing()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        connection.Pause(IntegrationsTestBed.Now);
        bed.Tester.Result = ConnectionTestResult.Unreachable("timeout");

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 2), Ct));

        Assert.Equal("integrations.connection.provider_unreachable", error.Code);
        Assert.Equal(ConnectionStatus.Paused, connection.Status);
        Assert.Equal(0, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task ResumingAnActiveConnectionIsNotPausedWithoutCallingTheProvider()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var error = await Assert.ThrowsAsync<IntegrationsDomainException>(() =>
            bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 1), Ct));

        Assert.Equal("integrations.connection.not_paused", error.Code);
        Assert.Empty(bed.Tester.Calls);
    }

    [Fact]
    public async Task DeleteRemovesAuditsAndPublishes()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        Assert.True(await bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 1), Ct));

        Assert.Empty(bed.Repository.Connections);
        Assert.Equal("integrations.connection.deleted", Assert.Single(bed.Audit.Entries).Action);
        Assert.Equal("integrations.connection-deleted.v1", Assert.Single(bed.Events.Published).EventName);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task DeleteWithAStaleVersionIs412AndKeepsTheConnection()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        await Assert.ThrowsAsync<RequestConcurrencyException>(() =>
            bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 9), Ct));

        Assert.Single(bed.Repository.Connections);
    }

    [Fact]
    public async Task AHiddenProviderBlocksTheLifecycleWith403()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.HideQuotations();

        Assert.Equal("tenancy.module_not_enabled", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct))).Code);
        Assert.Equal("tenancy.module_not_enabled", (await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct))).Code);
    }

    [Fact]
    public async Task WithoutManageEveryLifecycleCommandIsForbidden()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();
        bed.Permissions = [IntegrationsPermissions.ConnectionRead];

        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.TestHandler().HandleAsync(new TestConnectionCommand(bed.TenantId, connection.Id), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.PauseHandler().HandleAsync(new PauseConnectionCommand(bed.TenantId, connection.Id, 1), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.ResumeHandler().HandleAsync(new ResumeConnectionCommand(bed.TenantId, connection.Id, 1), Ct));
        await Assert.ThrowsAsync<RequestForbiddenException>(() =>
            bed.DeleteHandler().HandleAsync(new DeleteConnectionCommand(bed.TenantId, connection.Id, 1), Ct));
        Assert.Empty(bed.Tester.Calls);
    }
}
