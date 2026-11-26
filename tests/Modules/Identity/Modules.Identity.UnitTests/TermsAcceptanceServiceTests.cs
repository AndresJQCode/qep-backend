using Modules.Identity.Application;
using Modules.Identity.Domain;

namespace Modules.Identity.UnitTests;

/// <summary>
/// Spec 2026-11-19 (onboarding por link): la aceptación de términos se registra por usuario, con la versión vigente que sale
/// de configuración (<c>Terms:CurrentVersion</c>). Sin versión configurada el flujo falla cerrado: nunca acepta con una
/// versión inventada.
/// </summary>
public sealed class TermsAcceptanceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryTermsAcceptanceRepository acceptances = new();
    private readonly Guid userId = Guid.CreateVersion7();

    [Fact]
    public async Task AWrongVersionIsRejected()
    {
        var service = NewService("2026-11-19");

        var exception = await Assert.ThrowsAsync<IdentityDomainException>(() =>
            service.RecordAsync(userId, "2026-01-01", TestContext.Current.CancellationToken));

        Assert.Equal(TermsAcceptanceService.VersionMismatchCode, exception.Code);
        Assert.Equal("identity.terms.version_mismatch", exception.Code);
        Assert.Empty(acceptances.Stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutAConfiguredVersionNothingIsAccepted(string? configured)
    {
        var service = NewService(configured);
        var ct = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<IdentityDomainException>(() => service.RecordAsync(userId, "2026-11-19", ct));
        Assert.Equal(TermsAcceptanceService.UnavailableCode, exception.Code);
        Assert.Equal("identity.terms.unavailable", exception.Code);
        Assert.Empty(acceptances.Stored);

        // Tampoco se da por aceptada una versión que no existe.
        acceptances.Stored.Add((userId, "2026-11-19"));
        await Assert.ThrowsAsync<IdentityDomainException>(() => service.HasAcceptedCurrentAsync(userId, ct));
    }

    [Fact]
    public async Task HasAcceptedCurrentIsFalseForAnOlderVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await NewService("2026-01-01").RecordAsync(userId, "2026-01-01", ct);

        var current = NewService("2026-11-19");

        Assert.False(await current.HasAcceptedCurrentAsync(userId, ct));
        await current.RecordAsync(userId, " 2026-11-19 ", ct);
        Assert.True(await current.HasAcceptedCurrentAsync(userId, ct));
        Assert.False(await current.HasAcceptedCurrentAsync(Guid.CreateVersion7(), ct));
    }

    private TermsAcceptanceService NewService(string? currentVersion) =>
        new(acceptances, new TermsOptions { CurrentVersion = currentVersion }, new FixedClock(Now));

    private sealed class InMemoryTermsAcceptanceRepository : ITermsAcceptanceRepository
    {
        public List<(Guid UserId, string Version)> Stored { get; } = [];

        public Task<bool> AddIfMissingAsync(TermsAcceptance acceptance, CancellationToken cancellationToken)
        {
            if (Stored.Contains((acceptance.UserId.Value, acceptance.Version)))
            {
                return Task.FromResult(false);
            }

            Stored.Add((acceptance.UserId.Value, acceptance.Version));
            return Task.FromResult(true);
        }

        public Task<bool> ExistsAsync(Guid userId, string version, CancellationToken cancellationToken) =>
            Task.FromResult(Stored.Contains((userId, version)));
    }
}
