using Modules.Audit.Domain;
using Modules.Integrations.Application;
using Modules.Integrations.Domain;

namespace Modules.Integrations.UnitTests;

/// <summary>
/// Spec 2026-10-08, «Puertos para los consumidores»: <c>ResolveAsync</c> sólo entrega lo Active y
/// visible del tenant, con los secretos en claro y en memoria; el reporter pasa a NeedsAttention sin
/// umbral (D2).
/// </summary>
public sealed class ConsumerPortsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ResolveReturnsAnActiveVisibleConnectionWithItsSecretsInClear()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        var resolution = await bed.ConnectionsPort().ResolveAsync(bed.TenantId, connection.Id, Ct);

        Assert.Null(resolution.Unavailable);
        Assert.Equal(connection.Name, resolution.Name);
        var resolved = resolution.Connection;
        Assert.NotNull(resolved);
        Assert.Equal(connection.Id, resolved.Id);
        Assert.Equal("zenvia", resolved.ProviderKey);
        Assert.Equal(IntegrationsTestBed.FromNumber, resolved.Fields[ZenviaFieldKeys.FromNumber]);
        Assert.Equal(IntegrationsTestBed.Token, resolved.Secrets[ZenviaFieldKeys.ApiToken]);
        Assert.DoesNotContain(IntegrationsTestBed.Token, resolved.ToString(), StringComparison.Ordinal);
    }

    // Spec 2026-10-24 D-T4: el motivo de la primera comprobación que falla, con el nombre si la conexión existe.
    [Fact]
    public async Task ResolveExplainsWhyAConnectionCannotBeUsed()
    {
        var bed = new IntegrationsTestBed();
        var paused = bed.Seed("Pausada");
        paused.Pause(IntegrationsTestBed.Now);
        var attention = bed.Seed("Atención");
        attention.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);
        var foreign = bed.Seed("Ajena", tenantId: Guid.CreateVersion7());
        var unreadable = bed.Seed("Ilegible");
        bed.Protector.Unreadable.Add((unreadable.Id, ZenviaFieldKeys.ApiToken));
        var port = bed.ConnectionsPort();

        async Task<(ConnectionUnavailableReason?, string?, bool)> Of(Guid id)
        {
            var resolution = await port.ResolveAsync(bed.TenantId, id, Ct);
            return (resolution.Unavailable, resolution.Name, resolution.Connection is null);
        }

        Assert.Equal((ConnectionUnavailableReason.Paused, "Pausada", true), await Of(paused.Id));
        Assert.Equal((ConnectionUnavailableReason.NeedsAttention, "Atención", true), await Of(attention.Id));
        Assert.Equal((ConnectionUnavailableReason.NotFound, (string?)null, true), await Of(Guid.CreateVersion7()));
        Assert.Equal((ConnectionUnavailableReason.NotFound, (string?)null, true), await Of(foreign.Id));
        Assert.Equal((ConnectionUnavailableReason.SecretUnreadable, "Ilegible", true), await Of(unreadable.Id));

        var hidden = bed.Seed("Oculta");
        bed.HideQuotations();
        Assert.Equal((ConnectionUnavailableReason.ProviderHidden, "Oculta", true), await Of(hidden.Id));
    }

    // Zenvia exige que el operador lo habilite para el tenant (plan 2026-10-21): un conjunto vacío lo apaga.
    [Fact]
    public async Task ResolveSaysProviderDisabledWhenTheTenantTurnedItOff()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed("Apagada");
        bed.Providers.Sets[bed.TenantId] = new HashSet<string>(StringComparer.Ordinal);

        var resolution = await bed.ConnectionsPort().ResolveAsync(bed.TenantId, connection.Id, Ct);

        Assert.Null(resolution.Connection);
        Assert.Equal(ConnectionUnavailableReason.ProviderDisabled, resolution.Unavailable);
        Assert.Equal("Apagada", resolution.Name);
    }

    [Fact]
    public async Task ListActiveReturnsOnlyTheActiveOnesOfThatProviderByName()
    {
        var bed = new IntegrationsTestBed();
        bed.Seed("sur");
        bed.Seed("Norte");
        bed.Seed("Pausada").Pause(IntegrationsTestBed.Now);

        var active = await bed.ConnectionsPort().ListActiveAsync(bed.TenantId, "zenvia", Ct);

        Assert.Equal(["Norte", "sur"], active.Select(summary => summary.Name));
        Assert.Empty(await bed.ConnectionsPort().ListActiveAsync(bed.TenantId, "otro", Ct));
        bed.HideQuotations();
        Assert.Empty(await bed.ConnectionsPort().ListActiveAsync(bed.TenantId, "zenvia", Ct));
    }

    // Spec 2026-10-09 §6.1: la bandeja muestra el nombre también en una conexión pausada u oculta.
    [Fact]
    public async Task ListByProviderReturnsEveryStatusOfThatProviderByNameWithoutVisibility()
    {
        var bed = new IntegrationsTestBed();
        var south = bed.Seed("sur");
        var north = bed.Seed("Norte");
        var paused = bed.Seed("Pausada");
        paused.Pause(IntegrationsTestBed.Now);
        bed.Seed("Ajena", tenantId: Guid.CreateVersion7());
        bed.HideQuotations();

        var listing = await bed.ConnectionsPort().ListByProviderAsync(bed.TenantId, "zenvia", Ct);

        Assert.Equal(
            [(north.Id, "Norte", ConnectionStatus.Active), (paused.Id, "Pausada", ConnectionStatus.Paused), (south.Id, "sur", ConnectionStatus.Active)],
            listing.Select(entry => (entry.Id, entry.Name, entry.Status)));
        Assert.Empty(await bed.ConnectionsPort().ListByProviderAsync(bed.TenantId, "otro", Ct));
    }

    // D2 y P15: sin umbral; actor = la conexión, tipo Integration.
    [Fact]
    public async Task TheReporterMovesActiveToNeedsAttentionAuditsAsIntegrationAndPublishes()
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        await bed.HealthReporter().ReportCredentialsRejectedAsync(bed.TenantId, connection.Id, "credentials_rejected", Ct);

        Assert.Equal(ConnectionStatus.NeedsAttention, connection.Status);
        Assert.Equal("credentials_rejected", connection.LastFailureCode);
        var audit = Assert.Single(bed.Audit.Entries);
        Assert.Equal("integrations.connection.needs_attention", audit.Action);
        Assert.Equal(AuditActorType.Integration, audit.ActorType);
        Assert.Equal(connection.Id, audit.ActorId);
        Assert.Equal("integrations.connection-needs-attention.v1", Assert.Single(bed.Events.Published).EventName);
        Assert.Equal(1, bed.UnitOfWork.Saves);
    }

    [Fact]
    public async Task TheReporterIgnoresWhatIsNotActive()
    {
        var bed = new IntegrationsTestBed();
        var paused = bed.Seed("Pausada");
        paused.Pause(IntegrationsTestBed.Now);
        var attention = bed.Seed("Atención");
        attention.MarkNeedsAttention(ConnectionFailureCodes.CredentialsRejected, IntegrationsTestBed.Now);
        var reporter = bed.HealthReporter();

        await reporter.ReportCredentialsRejectedAsync(bed.TenantId, paused.Id, "credentials_rejected", Ct);
        await reporter.ReportCredentialsRejectedAsync(bed.TenantId, attention.Id, "credentials_rejected", Ct);
        await reporter.ReportCredentialsRejectedAsync(bed.TenantId, Guid.CreateVersion7(), "credentials_rejected", Ct);

        Assert.Equal(ConnectionStatus.Paused, paused.Status);
        Assert.Equal(0, bed.UnitOfWork.Saves);
        Assert.Empty(bed.Audit.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task TheReporterRejectsAFailureCodeThatDoesNotFit(string failureCode)
    {
        var bed = new IntegrationsTestBed();
        var connection = bed.Seed();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            bed.HealthReporter().ReportCredentialsRejectedAsync(bed.TenantId, connection.Id, failureCode, Ct));
    }
}
