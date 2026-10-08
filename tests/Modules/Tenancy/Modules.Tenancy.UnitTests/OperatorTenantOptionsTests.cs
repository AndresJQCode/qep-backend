using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Modules.Tenancy.Application;
using Modules.Tenancy.Infrastructure;

namespace Modules.Tenancy.UnitTests;

/// <summary>Spec 2026-10-08 §1: opcional en todos los ambientes, nunca Guid.Empty, y una advertencia
/// en producción si falta (D10).</summary>
public sealed class OperatorTenantOptionsTests
{
    private static readonly Guid QCode = Guid.Parse("01900000-0000-7000-8000-0000000000c0");

    [Fact]
    public void WithoutTheKeyStartupPassesAndNobodyIsOperator()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>());

        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.False(provider.GetRequiredService<IOperatorTenant>().IsOperator(QCode));
        Assert.False(provider.GetRequiredService<IOperatorTenant>().IsOperator(Guid.Empty));
    }

    [Fact]
    public void TheConfiguredTenantIsTheOnlyOperator()
    {
        using var provider = BuildProvider(new() { ["Platform:OperatorTenantId"] = QCode.ToString() });

        var operatorTenant = provider.GetRequiredService<IOperatorTenant>();

        Assert.True(operatorTenant.IsOperator(QCode));
        Assert.False(operatorTenant.IsOperator(Guid.CreateVersion7()));
    }

    [Fact]
    public void AnEmptyGuidFailsStartupValidation()
    {
        using var provider = BuildProvider(new() { ["Platform:OperatorTenantId"] = Guid.Empty.ToString() });

        var error = Assert.ThrowsAny<Exception>(provider.GetRequiredService<IStartupValidator>().Validate);

        Assert.Contains("Platform:OperatorTenantId", error.Message, StringComparison.Ordinal);
    }

    // Igual que el booleano de Entitlements (TenantModuleDefaultsTests): el binder lanza al arrancar.
    [Fact]
    public void ANonGuidFailsStartupValidation()
    {
        using var provider = BuildProvider(new() { ["Platform:OperatorTenantId"] = "qcode" });

        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IStartupValidator>().Validate);
    }

    [Fact]
    public async Task InProductionWithoutTheKeyStartupLogsAWarning()
    {
        var logger = new RecordingLogger<OperatorTenantStartupWarning>();
        var warning = new OperatorTenantStartupWarning(
            Options.Create(new OperatorTenantOptions()), new FixedEnvironment(Environments.Production), logger);

        await warning.StartAsync(TestContext.Current.CancellationToken);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Platform:OperatorTenantId", entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public async Task NoWarningWhenConfiguredOrOutsideProduction(string environment, bool configured)
    {
        var logger = new RecordingLogger<OperatorTenantStartupWarning>();
        var options = new OperatorTenantOptions { OperatorTenantId = configured ? QCode : null };
        var warning = new OperatorTenantStartupWarning(
            Options.Create(options), new FixedEnvironment(environment), logger);

        await warning.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(logger.Entries);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        values["ConnectionStrings:QepDatabase"] = "Host=localhost;Database=unused";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddTenancyInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Modules.Tenancy.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
