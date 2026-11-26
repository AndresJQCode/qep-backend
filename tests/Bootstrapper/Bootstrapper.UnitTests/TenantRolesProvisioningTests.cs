using Microsoft.Extensions.Logging;
using Modules.Authorization.Application;
using Modules.Tenancy.Domain;

namespace Bootstrapper.UnitTests;

/// <summary>
/// Spec 2026-11-25 (roles por tenant): el alta siembra los roles del tenant recién nacido, y un fallo de esa siembra se
/// registra como <c>Error</c> sin tumbar un alta que ya quedó confirmada. El siguiente arranque la repara.
/// </summary>
public sealed class TenantRolesProvisioningTests
{
    [Fact]
    public async Task ProvisioningSeedsTheNewTenant()
    {
        var seeder = new RecordingSeeder();
        var tenantId = TenantId.New();
        var provisioning = new TenantRolesProvisioning(seeder, new CapturingLogger());

        await provisioning.ProvisionAsync(tenantId, TestContext.Current.CancellationToken);

        Assert.Equal([tenantId.Value], seeder.Seeded);
    }

    [Fact]
    public async Task AFailedSeedIsLoggedAsErrorAndDoesNotThrow()
    {
        var failure = new InvalidOperationException("The database is gone.");
        var logger = new CapturingLogger();
        var tenantId = TenantId.New();
        var provisioning = new TenantRolesProvisioning(new RecordingSeeder(failure), logger);

        await provisioning.ProvisionAsync(tenantId, TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(tenantId.Value.ToString(), entry.Message, StringComparison.Ordinal);
        Assert.Same(failure, entry.Exception);
    }

    private sealed class RecordingSeeder(Exception? failure = null) : ITenantRoleSeeder
    {
        public List<Guid> Seeded { get; } = [];

        public Task<bool> SeedAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            if (failure is not null)
            {
                return Task.FromException<bool>(failure);
            }

            Seeded.Add(tenantId);
            return Task.FromResult(true);
        }

        public Task<int> SeedMissingAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<TenantRolesProvisioning>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
