using Bootstrapper.Valkey.LegalParameters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Modules.Hr.Application;

namespace Bootstrapper.UnitTests.Valkey;

/// <summary>
/// Con el área <c>LegalParameters</c> prendida, la tabla vive 1 hora en Valkey y sobrevive a los despliegues: una migración, un
/// hotfix de SQL o una restauración no suben la versión. El arranque la sube una vez para que cada despliegue empiece limpio.
/// </summary>
public sealed class LegalParameterCacheStartupBumpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingVersion(Exception? failure = null) : ILegalParameterCacheVersion
    {
        public int Bumps { get; private set; }

        public Task BumpAsync(CancellationToken cancellationToken)
        {
            Bumps++;
            return failure is null ? Task.CompletedTask : throw failure;
        }
    }

    [Fact]
    public async Task StartingBumpsTheVersionOnce()
    {
        var version = new RecordingVersion();
        var worker = new LegalParameterCacheStartupBump(version, NullLogger<LegalParameterCacheStartupBump>.Instance);

        await worker.StartAsync(Ct);

        Assert.Equal(1, version.Bumps);
    }

    [Fact]
    public async Task AFailingBumpNeverStopsTheStartup()
    {
        var version = new RecordingVersion(new InvalidOperationException("Valkey is down"));
        var worker = new LegalParameterCacheStartupBump(version, NullLogger<LegalParameterCacheStartupBump>.Instance);

        await worker.StartAsync(Ct);

        Assert.Equal(1, version.Bumps);
    }

    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    public void ItIsRegisteredOnlyWithTheAreaOn(string areaOn, int expected)
    {
        var services = ValkeyRegistrationTests.Register(
            ("Valkey:Areas:LegalParameters", areaOn),
            ("ConnectionStrings:QepDatabase", "Host=localhost;Database=unused;Username=unused;Password=unused"));
        var configuration = (Microsoft.Extensions.Configuration.IConfiguration)services
            .Single(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Configuration.IConfiguration)).ImplementationInstance!;
        Modules.Hr.Infrastructure.HrInfrastructureExtensions.AddHrInfrastructure(services, configuration);
        services.AddLegalParameterCache(configuration);

        var registered = services.Count(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(LegalParameterCacheStartupBump));

        Assert.Equal(expected, registered);
    }
}
